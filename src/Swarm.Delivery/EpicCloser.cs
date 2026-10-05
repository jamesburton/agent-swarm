using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Outcome of <c>epic close</c> (<see cref="EpicCloseResult.Result"/>).</summary>
public static class CloseResults
{
    /// <summary>Merged into the active branch (exit 0).</summary>
    public const string Merged = "merged";

    /// <summary>Not merged: blockers remain (exit 1).</summary>
    public const string Blocked = "blocked";

    /// <summary>Not merged: the merge conflicts (exit 1).</summary>
    public const string Conflict = "conflict";

    /// <summary>Checks passed; nothing changed (exit 0).</summary>
    public const string DryRun = "dry-run";
}

/// <summary>Options for <see cref="EpicCloser.Close"/>.</summary>
/// <param name="Into">Target branch, or null for the epic's base branch.</param>
/// <param name="Force">Waive waivable blockers (<see cref="EpicBlocker.Waivable"/>) only.</param>
/// <param name="DryRun">Check and build the message only.</param>
/// <param name="DeleteBranch">Delete the epic branch after merging, only while it still points at the merged tip.</param>
public sealed record CloseOptions(string? Into = null, bool Force = false, bool DryRun = false, bool DeleteBranch = false);

/// <summary>stdout of <c>epic close</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Result">A <see cref="CloseResults"/> value.</param>
/// <param name="Id">Epic id.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="Into">Target branch.</param>
/// <param name="MergeCommit">The merge commit (merged only).</param>
/// <param name="Tickets">Tickets from <c>Ticket:</c> trailers.</param>
/// <param name="Blockers">Blockers that stopped the close.</param>
/// <param name="Waived">Blockers waived by <c>--force</c>.</param>
/// <param name="ConflictFiles">Conflicting files (conflict only).</param>
/// <param name="Message">The merge message.</param>
/// <param name="BranchDeleted">True when the epic branch was deleted.</param>
/// <param name="Warnings">
/// One-line warnings that did not stop the close (temporary worktree left behind, epic branch kept or moved during close).
/// </param>
public sealed record EpicCloseResult(
    int SchemaVersion, string Result, string Id, string Branch, string Into, string? MergeCommit, IReadOnlyList<string> Tickets,
    IReadOnlyList<EpicBlocker> Blockers, IReadOnlyList<EpicBlocker> Waived, IReadOnlyList<string> ConflictFiles, string Message, bool BranchDeleted,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Closes an epic: <c>merge --no-ff</c> onto the active branch, never squashed. The merge is made in a temporary detached
/// worktree under the per-epic lock that batch run and squash run take, so no run can move the epic meanwhile; the active
/// branch then moves only from the commit the merge was built on (fast-forward of the worktree where it is checked out,
/// else a compare-and-swap ref update). Every failure before that move leaves the active branch, the epic record and the
/// epic branch untouched.
/// </summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicCloser(RepoPaths repo, SwarmConfig config)
{
    const string StaleLockHint =
        "if no other git process is running, remove the stale lock file git names (refs/heads/<branch>.lock, or reftable/tables.list.lock) and re-run epic close";

    /// <summary>Gets or sets a test seam invoked after the first assessment, just before the epic lock is taken.</summary>
    internal Action? BeforeLock { get; set; }

    /// <summary>Gets or sets a test seam invoked with the merge commit while the temporary worktree still exists, before the active branch moves.</summary>
    internal Action<string>? AfterMerge { get; set; }

    /// <summary>Closes an epic.</summary>
    /// <param name="id">Epic id.</param>
    /// <param name="options">Options.</param>
    /// <returns>The result (blocked, conflict and dry-run are results, not exceptions).</returns>
    /// <exception cref="ToolException">
    /// Unknown or closed epic, missing target (3); epic lock held, target moved or cannot be moved, failed fast-forward,
    /// failed merge or temporary worktree (4).
    /// </exception>
    public EpicCloseResult Close(string id, CloseOptions options)
    {
        var manager = new WorktreeManager(repo, config);
        var git = manager.Git;
        var store = new EpicStore(manager.State);
        var epic = OpenEpic(store, id);
        var assessor = new EpicAssessor(repo, config);
        var plan = Plan(git, epic, assessor.Assess(epic, options.Into), options);
        if (plan.Remaining.Count > 0)
        {
            return plan.Result(CloseResults.Blocked);
        }

        if (options.DryRun)
        {
            return plan.Result(CloseResults.DryRun);
        }

        BeforeLock?.Invoke();
        using var epicLock = Lock(manager, plan.Status.BatchEpic);

        // The assessment above is a point-in-time read: blockers may have appeared before the lock was taken, so assess
        // again under it (and re-read the record: another epic close takes the same lock). The lock reads as
        // batch-running only because this close now holds it.
        epic = OpenEpic(store, id);
        var status = assessor.Assess(epic, options.Into);
        plan = Plan(git, epic, status with { Blockers = status.Blockers.Where(b => epicLock is null || b.Code != BlockerCodes.BatchRunning).ToList() }, options);
        if (plan.Remaining.Count > 0)
        {
            return plan.Result(CloseResults.Blocked);
        }

        var into = plan.Status.Into;
        var tip = plan.Status.Tip!;
        var intoSha = git.RevParse(GitRunner.HeadsRef(into));
        var path = StatePaths.Guard(Path.Combine(manager.Root, "close-" + id), "close worktree");
        if (Discard(git, path) is { } stale)
        {
            throw new ToolException(ExitCodes.Environment, $"could not replace the previous close worktree; nothing merged: {stale}");
        }

        var warnings = new List<string>();
        string merge;
        Directory.CreateDirectory(manager.Root);
        try
        {
            git.Run("worktree", "add", "-q", "--detach", path, intoSha);
            var wt = git.At(path);
            var r = wt.Try("merge", "--no-ff", "--no-edit", "-m", plan.Message, tip);
            if (r.ExitCode != 0)
            {
                var files = wt.Lines("diff", "--name-only", "--diff-filter=U");
                if (files.Count == 0)
                {
                    throw new ToolException(ExitCodes.Environment, $"git merge of '{epic.Branch}' failed in '{path}': {TextLines.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut)}; nothing merged");
                }

                wt.Try("merge", "--abort");
                AddIfSet(warnings, Discard(git, path));
                return plan.Result(CloseResults.Conflict, files: files, warnings: warnings);
            }

            merge = wt.RevParse("HEAD");
            AfterMerge?.Invoke(merge);
        }
        catch (ToolException e)
        {
            throw WithCleanup(e, Discard(git, path));
        }

        AddIfSet(warnings, Discard(git, path));
        try
        {
            MoveActive(git, into, intoSha, merge, id);
        }
        catch (ToolException e)
        {
            throw WithCleanup(e, warnings.FirstOrDefault());
        }

        try
        {
            store.Save(epic with { State = EpicStates.Closed, ClosedUtc = DateTime.UtcNow, MergeCommit = merge, MergedInto = into });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ToolException)
        {
            throw new ToolException(
                ExitCodes.Environment,
                $"merged '{epic.Branch}' into '{into}' at {merge}, but could not record the epic as closed: {TextLines.OneLine(e.Message)}",
                $"fix the state directory, then set state, mergeCommit and mergedInto in {store.PathOf(id)} by hand");
        }

        var deleted = Finish(git, epic.Branch, into, tip, options.DeleteBranch, warnings);
        return plan.Result(CloseResults.Merged, merge, deleted: deleted, warnings: warnings);
    }

    static EpicRecord OpenEpic(EpicStore store, string id)
    {
        var epic = store.Get(id);
        return epic.State == EpicStates.Closed
            ? throw new ToolException(ExitCodes.BadInput, $"epic '{id}' is already closed (merged into '{epic.MergedInto}' at {epic.MergeCommit})")
            : epic;
    }

    // Applies --force (waivable blockers only) and builds the message for one assessment.
    static ClosePlan Plan(GitRunner git, EpicRecord epic, EpicStatus status, CloseOptions options)
    {
        IReadOnlyList<EpicBlocker> waived = options.Force ? status.Blockers.Where(b => b.Waivable).ToList() : [];
        var remaining = status.Blockers.Except(waived).ToList();
        IReadOnlyList<TrailerCommit> commits = status.Tip is null ? [] : TrailerLog.Read(git, GitRunner.HeadsRef(status.Into), status.Tip);
        return new ClosePlan(epic, status, commits, waived, remaining, MergeMessage.Build(epic, status.BatchEpic, status.Into, commits));
    }

    // The per-epic lock batch run and squash run share (the same construction as theirs). Without a batch epic id neither
    // tool can reach the epic branch, so there is no lock to take.
    SlotLease? Lock(WorktreeManager manager, string? batchEpic)
    {
        if (batchEpic is null)
        {
            return null;
        }

        var lockDir = manager.State.BatchLockDir(batchEpic);
        return new SlotSemaphore(lockDir, SlotOptions.From(config) with { Slots = 1, MaxWait = null }).TryAcquire("epic close")
            ?? throw new ToolException(ExitCodes.Environment, $"a batch or squash run holds epic '{batchEpic}'; nothing merged", $"wait for it to finish, then re-run epic close; lock in {lockDir}");
    }

    // Checked out somewhere (normally the user's main worktree, kept clean by the active-dirty blocker): fast-forward it so
    // its files follow. Not checked out: compare-and-swap the ref.
    static void MoveActive(GitRunner git, string into, string intoSha, string merge, string id)
    {
        if (WorktreeList.CheckedOut(git, into) is { Prunable: false } active && Directory.Exists(active.Path))
        {
            var at = git.At(active.Path);
            if (!string.Equals(at.RevParse("HEAD"), intoSha, StringComparison.OrdinalIgnoreCase))
            {
                throw Moved(into);
            }

            var ff = at.Try("merge", "--ff-only", "--no-stat", merge);
            if (ff.ExitCode != 0)
            {
                throw StillAt(git, into, intoSha)
                    ? new ToolException(
                        ExitCodes.Environment,
                        $"could not fast-forward '{into}' in '{active.Path}': {TextLines.OneLine(ff.StdErr.Length > 0 ? ff.StdErr : ff.StdOut)}; nothing merged",
                        "move untracked files that the merge would overwrite; " + StaleLockHint)
                    : Moved(into);
            }

            return;
        }

        var r = git.Try("update-ref", "-m", $"epic close {id}", GitRunner.HeadsRef(into), merge, intoSha);
        if (r.ExitCode == 0)
        {
            return;
        }

        // update-ref also fails when the ref cannot be locked (as Plan B's EpicRef.Move learned): only a changed tip
        // means another writer moved it.
        throw StillAt(git, into, intoSha)
            ? new ToolException(ExitCodes.Environment, $"could not move '{into}': {TextLines.OneLine(r.StdErr)}; nothing merged", StaleLockHint)
            : Moved(into);
    }

    static bool StillAt(GitRunner git, string branch, string sha) =>
        string.Equals(git.Try("rev-parse", "--verify", "-q", GitRunner.HeadsRef(branch)).StdOut.Trim(), sha, StringComparison.OrdinalIgnoreCase);

    static ToolException Moved(string into) =>
        new(ExitCodes.Environment, $"'{into}' moved during close; nothing merged", "re-run epic close");

    // After the merge: report an epic branch that moved meanwhile (its new commits are not merged) and delete the branch
    // when asked, only while it still points at the merged tip and is checked out nowhere.
    static bool Finish(GitRunner git, string branch, string into, string tip, bool delete, List<string> warnings)
    {
        if (!StillAt(git, branch, tip))
        {
            warnings.Add($"'{branch}' moved during close; only its commits up to {tip[..7]} are merged into '{into}'; the branch was kept");
            return false;
        }

        if (!delete)
        {
            return false;
        }

        if (WorktreeList.CheckedOut(git, branch) is { } w)
        {
            warnings.Add($"'{branch}' was kept: it is checked out at '{w.Path}'");
            return false;
        }

        var r = git.Try("update-ref", "-m", "epic close: delete merged epic branch", "-d", GitRunner.HeadsRef(branch), tip);
        if (r.ExitCode != 0)
        {
            warnings.Add(StillAt(git, branch, tip)
                ? $"'{branch}' was kept: {TextLines.OneLine(r.StdErr)}"
                : $"'{branch}' moved during close and was kept; only its commits up to {tip[..7]} are merged into '{into}'");
            return false;
        }

        git.Try("config", "--remove-section", $"branch.{branch}");
        return true;
    }

    // Removes the temporary worktree (this path only: a repo-wide prune would also drop unrelated registrations).
    // Returns null when it is gone, else a one-line account of what is left (Windows can hold files open).
    static string? Discard(GitRunner git, string path)
    {
        var left = new List<string>();
        try
        {
            if (Registered(git, path))
            {
                git.Try("worktree", "remove", "--force", path);
            }
        }
        catch (ToolException)
        {
            // Reported below from what is actually left.
        }

        try
        {
            FileTree.DeleteTree(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reported below from what is actually left.
        }

        if (Directory.Exists(path))
        {
            left.Add($"'{path}' left behind (a file may be in use), delete it manually");
        }

        try
        {
            if (Registered(git, path))
            {
                left.Add($"worktree '{path}' still registered, run: git worktree remove --force \"{path}\"");
            }
        }
        catch (ToolException)
        {
            left.Add("worktree registration unknown, check git worktree list");
        }

        return left.Count == 0 ? null : "temporary close worktree not removed: " + string.Join("; ", left);
    }

    static bool Registered(GitRunner git, string path) => WorktreeList.Read(git).Any(w => WorktreeList.SamePath(w.Path, path));

    static void AddIfSet(List<string> warnings, string? warning)
    {
        if (warning is not null)
        {
            warnings.Add(warning);
        }
    }

    static ToolException WithCleanup(ToolException e, string? cleanup) =>
        cleanup is null ? e : new ToolException(e.ExitCode, $"{e.Message}; {cleanup}", e.Hint);

    sealed record ClosePlan(
        EpicRecord Epic, EpicStatus Status, IReadOnlyList<TrailerCommit> Commits, IReadOnlyList<EpicBlocker> Waived, IReadOnlyList<EpicBlocker> Remaining, string Message)
    {
        public EpicCloseResult Result(
            string result, string? merge = null, IReadOnlyList<string>? files = null, bool deleted = false, IReadOnlyList<string>? warnings = null) =>
            new(
                SwarmJson.SchemaVersion, result, Epic.Id, Epic.Branch, Status.Into, merge, MergeMessage.Tickets(Commits), Remaining, Waived, files ?? [], Message,
                deleted, warnings ?? []);
    }
}
