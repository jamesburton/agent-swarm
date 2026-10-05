using System.Globalization;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>A worktree to create.</summary>
/// <param name="Ticket">Ticket id (a safe name).</param>
/// <param name="Slug">Branch slug.</param>
/// <param name="BaseBranch">Branch to start from (normally the epic branch).</param>
/// <param name="Kind">Value for <c>{kind}</c>, or null for the configured default.</param>
public sealed record CreateRequest(string Ticket, string Slug, string BaseBranch, string? Kind);

/// <summary>stdout of <c>worktree create</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Created">False when the worktree already existed (idempotent repeat).</param>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Task branch.</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Base">Base branch.</param>
/// <param name="Head">Current head commit.</param>
/// <param name="Warnings">One-line warnings (e.g. MAX_PATH).</param>
public sealed record WorktreeCreateResult(int SchemaVersion, bool Created, string Path, string Branch, string Ticket, string Base, string Head, IReadOnlyList<string> Warnings);

/// <summary>A worktree with its swarm state.</summary>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Checked-out branch.</param>
/// <param name="Ticket">Ticket id (managed only).</param>
/// <param name="Base">Base branch (managed only).</param>
/// <param name="BaseExists">True when the base branch still exists.</param>
/// <param name="Head">Head commit.</param>
/// <param name="Locked">True when locked.</param>
/// <param name="LockReason">Lock reason.</param>
/// <param name="Missing">True when the directory is gone or git reports the registration prunable (see <c>DirectoryExists</c>).</param>
/// <param name="Dirty">True with uncommitted or untracked changes.</param>
/// <param name="Empty">True when the head is still the fork point (no commits).</param>
/// <param name="AheadOfBase">Commits since the fork point.</param>
/// <param name="MergedVia">A <see cref="MergeVia"/> value, or null.</param>
/// <param name="Managed">True when created by this tool (branch metadata present).</param>
/// <param name="Error">
/// One-line reason the worktree's state could not be assessed, or null. When set, the state fields are conservative
/// (<c>Dirty</c> true, <c>Empty</c> false, <c>AheadOfBase</c> 0, <c>MergedVia</c> null) and the worktree must be kept.
/// </param>
/// <param name="DirectoryExists">
/// True when the worktree directory exists on disk. With <c>Missing</c> also true, git's registration is stale (e.g. the
/// directory's <c>.git</c> file is gone) but the directory may still hold work, so it is not disposable.
/// </param>
public sealed record WorktreeEntry(
    string Path, string Branch, string? Ticket, string? Base, bool BaseExists, string? Head, bool Locked, string? LockReason,
    bool Missing, bool Dirty, bool Empty, int AheadOfBase, string? MergedVia, bool Managed, string? Error = null, bool DirectoryExists = true);

/// <summary>stdout of <c>worktree list</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Root">Worktree root.</param>
/// <param name="Worktrees">The worktrees.</param>
public sealed record WorktreeListResult(int SchemaVersion, string Root, IReadOnlyList<WorktreeEntry> Worktrees);

/// <summary>Creates and inspects per-task worktrees.</summary>
public sealed class WorktreeManager
{
    /// <summary>Longest path Windows tools without long-path support accept.</summary>
    public const int WindowsMaxPath = 259;

    const int MetaWriteAttempts = 5;
    const int MetaWriteDelayMs = 100;

    readonly SwarmConfig config;

    /// <summary>Initializes a new instance of the <see cref="WorktreeManager"/> class.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="config">Validated config.</param>
    /// <exception cref="ToolException">State dir or worktree root path invalid (exit code 2).</exception>
    public WorktreeManager(RepoPaths repo, SwarmConfig config)
    {
        this.config = config;
        Git = new GitRunner(repo.MainWorktreeRoot);
        Root = StatePaths.ResolveWorktreeRoot(repo, config.WorktreeRoot);
        State = new StateLayout(StatePaths.Resolve(repo, config.StateDir));
    }

    /// <summary>Gets or sets a test seam invoked with the branch name just before <c>git worktree add</c>.</summary>
    internal Action<string>? BeforeWorktreeAdd { get; set; }

    /// <summary>Gets the worktree root.</summary>
    public string Root { get; }

    /// <summary>Gets a runner in the main worktree.</summary>
    public GitRunner Git { get; }

    /// <summary>Gets the run-state layout.</summary>
    public StateLayout State { get; }

    /// <summary>Gets the path for a ticket's worktree.</summary>
    /// <param name="ticket">Ticket id.</param>
    /// <returns><c>&lt;root&gt;/t-&lt;ticket&gt;</c>.</returns>
    /// <exception cref="ToolException">Too long (exit code 2).</exception>
    public string PathFor(string ticket) => StatePaths.Guard(Path.Combine(Root, "t-" + ticket), "task worktree");

    /// <summary>Creates a task worktree on a new branch from the base (idempotent for the same branch and base).</summary>
    /// <param name="request">The request.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">Naming or path (2), base/branch/lock state (3), foreign directory or git failure (4).</exception>
    public WorktreeCreateResult Create(CreateRequest request)
    {
        var branch = BranchTemplate.Render(config.Worktree, request.Ticket, request.Slug, request.Kind);
        if (Git.Try("check-ref-format", "--branch", branch).ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Usage, $"branch name '{branch}' is not valid for git");
        }

        var path = PathFor(request.Ticket);
        if (!Git.RefExists(GitRunner.HeadsRef(request.BaseBranch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"base branch '{request.BaseBranch}' not found", "open the epic first: epic open <id> <slug>");
        }

        var worktrees = WorktreeList.Read(Git);
        if (worktrees.FirstOrDefault(w => w.Branch == branch) is { } existing)
        {
            var meta = BranchMetaStore.ReadAll(Git).GetValueOrDefault(branch);
            return meta?.Base == request.BaseBranch
                ? new WorktreeCreateResult(SwarmJson.SchemaVersion, false, existing.Path, branch, request.Ticket, request.BaseBranch, existing.Head ?? "", [])
                : throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' is already checked out at '{existing.Path}' (base '{meta?.Base ?? "unknown"}')", "use another ticket or slug");
        }

        if (Git.RefExists(GitRunner.HeadsRef(branch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' already exists without a worktree", "delete it or use another slug");
        }

        if (worktrees.FirstOrDefault(w => WorktreeList.SamePath(w.Path, path)) is { } registered)
        {
            if (registered.Locked)
            {
                throw new ToolException(ExitCodes.BadInput, $"'{path}' is registered as a locked worktree ({registered.LockReason ?? "no reason given"})", "git worktree unlock it first");
            }

            if (!registered.Prunable)
            {
                throw new ToolException(ExitCodes.BadInput, $"'{path}' is already a worktree of branch '{registered.Branch ?? "(detached)"}'");
            }

            // A deleted directory with stale registration: drop the registration so the path can be reused.
            Git.Run("worktree", "prune");
        }

        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new ToolException(ExitCodes.Environment, $"'{path}' exists and is not empty", "remove it or change worktreeRoot");
        }

        // Start from the base's sha (not its name) so the fork point recorded below is exactly what was checked out.
        var forkPoint = Git.RevParse(GitRunner.HeadsRef(request.BaseBranch));
        Directory.CreateDirectory(Root);
        var dirPreExisted = Directory.Exists(path);
        BeforeWorktreeAdd?.Invoke(branch);
        var add = Git.Try("worktree", "add", "-q", "-b", branch, path, forkPoint);
        if (add.ExitCode != 0)
        {
            // git may leave the branch it just created behind when checkout fails; RollBack deletes it only while it still points at forkPoint.
            throw RollBack(branch, forkPoint, path, false, dirPreExisted, TextLines.OneLine(add.StdErr.Length > 0 ? add.StdErr : add.StdOut), "git worktree add failed");
        }

        WriteMetaOrRollBack(new BranchMeta(branch, request.Ticket, request.BaseBranch, forkPoint), path);
        return new WorktreeCreateResult(SwarmJson.SchemaVersion, true, path, branch, request.Ticket, request.BaseBranch, forkPoint, MaxPathWarnings(path, forkPoint));
    }

    /// <summary>Lists worktrees with their state.</summary>
    /// <param name="baseBranch">Only managed worktrees on this base, or null for all bases.</param>
    /// <param name="all">Also include unmanaged worktrees on a branch (ignored when <paramref name="baseBranch"/> is set).</param>
    /// <returns>
    /// The entries in git's order. A worktree whose state cannot be read (e.g. a corrupt <c>.git</c> file) is still listed,
    /// with <see cref="WorktreeEntry.Error"/> set and conservative state, so one bad worktree never hides the others.
    /// </returns>
    public IReadOnlyList<WorktreeEntry> List(string? baseBranch = null, bool all = false)
    {
        var metas = BranchMetaStore.ReadAll(Git);
        var history = RunHistory.Load(State);
        var ledgers = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var entries = new List<WorktreeEntry>();
        foreach (var w in WorktreeList.Read(Git).Where(w => w.Branch is not null))
        {
            var meta = metas.GetValueOrDefault(w.Branch!);
            if ((meta is null && (!all || baseBranch is not null)) || (baseBranch is not null && meta?.Base != baseBranch))
            {
                continue;
            }

            entries.Add(Assess(w, meta, history, ledgers));
        }

        return entries;
    }

    // Reads one worktree's state; a git failure here is confined to this entry (reported in Error, state left conservative).
    WorktreeEntry Assess(GitWorktree w, BranchMeta? meta, RunHistory history, Dictionary<string, IReadOnlySet<string>> ledgers)
    {
        var directoryExists = Directory.Exists(w.Path);
        var missing = w.Prunable || !directoryExists;
        var baseExists = meta is not null && Git.RefExists(GitRunner.HeadsRef(meta.Base));
        var entry = new WorktreeEntry(w.Path, w.Branch!, meta?.Ticket, meta?.Base, baseExists, w.Head, w.Locked, w.LockReason, missing, false, false, 0, null, meta is not null, null, directoryExists);
        try
        {
            var dirty = !missing && Git.At(w.Path).Run("status", "--porcelain").Length > 0;
            if (meta is null)
            {
                return entry with { Dirty = dirty };
            }

            var empty = string.Equals(w.Head, meta.ForkPoint, StringComparison.OrdinalIgnoreCase);
            var ahead = int.Parse(Git.Run("rev-list", "--count", $"{meta.ForkPoint}..{GitRunner.HeadsRef(w.Branch!)}"), CultureInfo.InvariantCulture);
            var via = empty ? null : MergeCheck.LandedVia(Git, w.Branch!, baseExists ? meta.Base : config.BaseBranch, LedgerFor(history, ledgers, meta.Base, baseExists));
            return entry with { Dirty = dirty, Empty = empty, AheadOfBase = ahead, MergedVia = via };
        }
        catch (ToolException e)
        {
            // Unknown state is reported as dirty and unmerged, so nothing downstream treats it as disposable.
            return entry with { Dirty = true, Error = TextLines.OneLine(e.Message) };
        }
    }

    // The three config writes can lose a race for .git/config.lock; retry briefly, then undo the worktree and branch so a failed create leaves nothing behind.
    void WriteMetaOrRollBack(BranchMeta meta, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                BranchMetaStore.Write(Git, meta);
                return;
            }
            catch (ToolException e)
            {
                if (attempt < MetaWriteAttempts)
                {
                    Thread.Sleep(MetaWriteDelayMs);
                    continue;
                }

                throw RollBack(meta.Branch, meta.ForkPoint, path, true, false, e.Message.ReplaceLineEndings(" ").Trim(), $"could not record metadata for '{meta.Branch}'");
            }
        }
    }

    // Undoes a half-made create and reports what actually happened (git can fail to delete a worktree directory that has a file open on Windows).
    // The branch is deleted only while it still points at forkPoint, so a branch another process created meanwhile is never touched.
    ToolException RollBack(string branch, string forkPoint, string path, bool removeWorktree, bool dirPreExisted, string cause, string what)
    {
        var removed = removeWorktree && Git.Try("worktree", "remove", "--force", path).ExitCode == 0;
        var left = new List<string>();
        var branchRef = GitRunner.HeadsRef(branch);
        var branchAbsent = false;
        if (Git.Try("update-ref", "-d", branchRef, forkPoint).ExitCode == 0)
        {
            branchAbsent = true;
            Git.Try("config", "--remove-section", $"branch.{branch}");
        }
        else if (!Git.RefExists(branchRef))
        {
            branchAbsent = true;
        }
        else
        {
            left.Add($"branch '{branch}' left untouched (it does not point at the commit this call started from)");
        }

        if (Directory.Exists(path) && !dirPreExisted)
        {
            left.Add($"'{path}' left behind (a file may be in use), delete it manually");
        }

        try
        {
            if (WorktreeList.Read(Git).Any(w => WorktreeList.SamePath(w.Path, path)))
            {
                left.Add($"worktree '{path}' still registered, run: git worktree remove --force");
            }
        }
        catch (ToolException)
        {
            left.Add("worktree registration unknown, check git worktree list");
        }

        var state = left.Count == 0
            ? (removeWorktree && removed ? "worktree and branch removed" : "branch removed")
            : (branchAbsent ? "branch removed; " : string.Empty) + "rollback incomplete: " + string.Join("; ", left);
        return new ToolException(ExitCodes.Environment, $"{what} ({state}): {cause}");
    }

    // The run ledger is epic-scoped (and tip-date guarded); a worktree whose base is gone has no epic to ask, so it gets an empty ledger.
    IReadOnlySet<string> LedgerFor(RunHistory history, Dictionary<string, IReadOnlySet<string>> cache, string epicBranch, bool baseExists)
    {
        if (!baseExists)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (!cache.TryGetValue(epicBranch, out var set))
        {
            cache[epicBranch] = set = history.LandedBranches(Git, epicBranch);
        }

        return set;
    }

    IReadOnlyList<string> MaxPathWarnings(string path, string commit)
    {
        var longest = Git.Lines("ls-tree", "-r", "--name-only", commit).Select(l => l.Length).DefaultIfEmpty(0).Max();
        var total = path.Length + 1 + longest;
        return total > WindowsMaxPath
            ? [$"deepest file path in the worktree will be {total} chars (> {WindowsMaxPath}); tools without long-path support may fail there; shorten worktreeRoot"]
            : [];
    }
}
