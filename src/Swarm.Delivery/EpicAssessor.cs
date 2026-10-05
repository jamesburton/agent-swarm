using System.Globalization;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Reasons an epic cannot be closed (<see cref="EpicBlocker.Code"/>).</summary>
public static class BlockerCodes
{
    /// <summary>
    /// A batch or squash run holds the per-epic lock they share (not waivable). A lock with a fresh heartbeat always
    /// counts (<see cref="SlotSemaphore.BlocksAcquire"/>), so a crashed local run blocks until its lock is older than
    /// <c>expirySec</c>.
    /// </summary>
    public const string BatchRunning = "batch-running";

    /// <summary>A batch run has no summary: crashed or killed (waivable).</summary>
    public const string RunUnfinished = "run-unfinished";

    /// <summary>Tasks returned or left unprocessed by their latest run (waivable).</summary>
    public const string TasksReturned = "tasks-returned";

    /// <summary>Task worktrees on the epic hold unmerged or uncommitted work (waivable).</summary>
    public const string WorktreesUnmerged = "worktrees-unmerged";

    /// <summary>The epic has no commits beyond the active branch (not waivable).</summary>
    public const string NothingToMerge = "nothing-to-merge";

    /// <summary>The active branch's worktree has tracked changes (not waivable).</summary>
    public const string ActiveDirty = "active-dirty";

    /// <summary>The active branch is behind its last-fetched upstream (not waivable).</summary>
    public const string ActiveBehindUpstream = "active-behind-upstream";

    /// <summary>The epic is already closed (not waivable).</summary>
    public const string EpicClosed = "epic-closed";

    /// <summary>The epic branch does not exist (not waivable).</summary>
    public const string BranchMissing = "branch-missing";
}

/// <summary>One reason an epic cannot be closed.</summary>
/// <param name="Code">A <see cref="BlockerCodes"/> value.</param>
/// <param name="Detail">One-line explanation.</param>
/// <param name="Waivable">True when <c>epic close --force</c> may ignore it.</param>
public sealed record EpicBlocker(string Code, string Detail, bool Waivable);

/// <summary>A task that has not landed.</summary>
/// <param name="Task">Task id.</param>
/// <param name="State">A <see cref="TaskStates"/> value.</param>
/// <param name="Branch">Worker branch, when known.</param>
/// <param name="Final">The return record's final state.</param>
/// <param name="Kind">The return record's kind.</param>
/// <param name="RunId">Run that decided the state.</param>
/// <param name="Reason">The return reason (git's output for a land-stage return without files).</param>
public sealed record TaskReturn(string Task, string State, string Branch, string? Final, string? Kind, string RunId, string? Reason);

/// <summary>Run state and close readiness of one epic.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Id">Epic id.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="State">An <see cref="EpicStates"/> value.</param>
/// <param name="Into">Active branch it would merge into.</param>
/// <param name="Tip">Epic tip, or null when the branch is missing.</param>
/// <param name="Ahead">Epic commits not on <paramref name="Into"/>.</param>
/// <param name="Behind"><paramref name="Into"/> commits not on the epic.</param>
/// <param name="BatchEpic">The batch epic id, or null.</param>
/// <param name="Runs">Batch runs of this epic.</param>
/// <param name="LatestRunId">Newest run.</param>
/// <param name="LatestRunExitCode">Its exit code (null when unfinished).</param>
/// <param name="LandedTasks">Tasks landed across runs.</param>
/// <param name="OpenTasks">Tasks returned or unprocessed.</param>
/// <param name="Worktrees">Managed task worktrees on the epic.</param>
/// <param name="WorktreesUnmerged">Of those, with unmerged or uncommitted work.</param>
/// <param name="BatchRunning">True when the epic lock is held by the acquire rule (a fresh heartbeat always counts).</param>
/// <param name="Upstream">Active branch's upstream (last fetched), or null.</param>
/// <param name="Blockers">Reasons it cannot close now.</param>
/// <param name="ReadyToClose">True when there are no blockers.</param>
public sealed record EpicStatus(
    int SchemaVersion, string Id, string Slug, string Branch, string State, string Into, string? Tip, int Ahead, int Behind, string? BatchEpic,
    int Runs, string? LatestRunId, int? LatestRunExitCode, IReadOnlyList<string> LandedTasks, IReadOnlyList<TaskReturn> OpenTasks,
    int Worktrees, int WorktreesUnmerged, bool BatchRunning, string? Upstream, IReadOnlyList<EpicBlocker> Blockers, bool ReadyToClose);

/// <summary>stdout of <c>epic status</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Epics">One status per epic, by id.</param>
/// <param name="Unreadable">Record files that could not be read (each is also a stderr warning); empty for <c>status &lt;id&gt;</c>.</param>
public sealed record EpicStatusList(int SchemaVersion, IReadOnlyList<EpicStatus> Epics, IReadOnlyList<EpicFileError> Unreadable);

/// <summary>Computes an epic's status and close blockers (one function for status and close).</summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicAssessor(RepoPaths repo, SwarmConfig config)
{
    /// <summary>Assesses an epic.</summary>
    /// <param name="epic">The epic record.</param>
    /// <param name="into">Target branch, or null for the epic's base branch.</param>
    /// <returns>The status.</returns>
    /// <exception cref="ToolException">The target branch does not exist (exit code 3).</exception>
    public EpicStatus Assess(EpicRecord epic, string? into = null)
    {
        var manager = new WorktreeManager(repo, config);
        var git = manager.Git;
        into ??= epic.BaseBranch;
        if (!git.RefExists(GitRunner.HeadsRef(into)))
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{into}' not found");
        }

        var blockers = new List<EpicBlocker>();
        if (epic.State == EpicStates.Closed)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.EpicClosed, $"epic '{epic.Id}' was merged into '{epic.MergedInto}' at {epic.MergeCommit}", false));
        }

        string? tip = null;
        int ahead = 0, behind = 0;
        if (!git.RefExists(GitRunner.HeadsRef(epic.Branch)))
        {
            blockers.Add(new EpicBlocker(BlockerCodes.BranchMissing, $"epic branch '{epic.Branch}' not found", false));
        }
        else
        {
            tip = git.RevParse(GitRunner.HeadsRef(epic.Branch));
            var counts = git.Run("rev-list", "--left-right", "--count", $"{GitRunner.HeadsRef(into)}...{GitRunner.HeadsRef(epic.Branch)}").Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            (behind, ahead) = (int.Parse(counts[0], CultureInfo.InvariantCulture), int.Parse(counts[1], CultureInfo.InvariantCulture));
            if (ahead == 0 && epic.State == EpicStates.Open)
            {
                blockers.Add(new EpicBlocker(BlockerCodes.NothingToMerge, $"'{epic.Branch}' has no commits that are not on '{into}'", false));
            }
        }

        // batch run and squash run share this per-epic lock (slot 0 for the whole run). Without a batch epic id the
        // epic branch cannot be reached through epicBranchTemplate, so neither tool can run on it and there is no lock.
        // "Running" uses the acquire rule (SlotSemaphore.BlocksAcquire): a fresh heartbeat always counts. Known limitation:
        // a genuinely crashed local run keeps blocking (not waivable) until its lock is older than expirySec.
        var batchEpic = EpicNaming.BatchEpicId(config, epic.Branch);
        var running = batchEpic is not null
            && new SlotSemaphore(manager.State.BatchLockDir(batchEpic), SlotOptions.From(config) with { Slots = 1 }).Status().Any(SlotSemaphore.BlocksAcquire);
        if (running)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.BatchRunning, $"a batch or squash run holds epic '{batchEpic}'; wait for it to finish", false));
        }

        // A run is finished iff it has a readable summary.json. Known gaps (RunHistory tolerates both, nothing crashes):
        // a run that crashed before its run-start event has no epic and is invisible here, so it raises no run-unfinished
        // blocker; and RunDirectories.Prune keeps the newest keepRuns finished runs across ALL epics, so a busy epic can
        // delete another epic's run folders and with them the evidence for tasks-returned (Plan A; fix is a per-epic prune).
        var history = RunHistory.Load(manager.State);
        var runs = history.ForEpic(epic.Branch);
        var unfinished = runs.Where(r => !r.Finished).Select(r => r.RunId).ToList();
        if (!running && unfinished.Count > 0)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.RunUnfinished, $"batch run(s) without summary.json (crashed or killed): {string.Join(", ", unfinished)}", true));
        }

        // squash run writes no run state (C5): a task batch returned and a human then landed with squash run is found on
        // the epic by MergeCheck's ancestor or content check. The ledger is epic-scoped and only trusts a branch whose tip is
        // no newer than the run that landed it (ruling C4).
        var ledger = history.LandedBranches(git, epic.Branch);
        bool LandedSince(TaskOutcome o) =>
            tip is not null
            && o.Branch.Length > 0
            && git.RefExists(GitRunner.HeadsRef(o.Branch))
            && MergeCheck.LandedVia(git, o.Branch, epic.Branch, ledger) is not null;

        var outcomes = RunHistory.Outcomes(runs).Values.ToList();
        var open = outcomes
            .Where(o => o.Blocking && !LandedSince(o))
            .Select(o => new TaskReturn(o.Task, o.State, o.Branch, o.LastReturn?.Final, o.LastReturn?.Kind, o.RunId, ReasonOf(o.LastReturn)))
            .ToList();
        if (open.Count > 0)
        {
            var list = string.Join(", ", open.Select(t => $"{t.Task} ({t.Final ?? t.State})"));
            blockers.Add(new EpicBlocker(BlockerCodes.TasksReturned, $"{open.Count} task(s) not landed: {list}", true));
        }

        var worktrees = manager.List(epic.Branch);
        var unmerged = worktrees.Select(w => (Worktree: w, Why: UnmergedReason(w))).Where(u => u.Why is not null).ToList();
        if (unmerged.Count > 0)
        {
            blockers.Add(new EpicBlocker(
                BlockerCodes.WorktreesUnmerged,
                "unmerged work in task worktrees: " + string.Join(", ", unmerged.Select(u => $"{u.Worktree.Ticket} ({u.Worktree.Branch}{u.Why})")),
                true));
        }

        // The user's own line-ending settings: under the runner's fixed core.autocrlf=false a clean CRLF checkout reads as modified.
        if (WorktreeList.CheckedOut(git, into) is { Prunable: false } active
            && Directory.Exists(active.Path)
            && git.WithRepoLineEndings().At(active.Path).Run("status", "--porcelain", "--untracked-files=no").Length > 0)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.ActiveDirty, $"'{into}' is checked out at '{active.Path}' with uncommitted changes to tracked files", false));
        }

        var up = git.Try("rev-parse", "--abbrev-ref", "--symbolic-full-name", into + "@{upstream}");
        var upstream = up.ExitCode == 0 ? up.StdOut.Trim() : null;
        if (upstream is not null)
        {
            var missing = int.Parse(git.Run("rev-list", "--count", $"{GitRunner.HeadsRef(into)}..{upstream}"), CultureInfo.InvariantCulture);
            if (missing > 0)
            {
                blockers.Add(new EpicBlocker(BlockerCodes.ActiveBehindUpstream, $"'{into}' is {missing} commit(s) behind '{upstream}' as last fetched; pull first", false));
            }
        }

        var latest = runs.LastOrDefault();
        return new EpicStatus(
            SwarmJson.SchemaVersion, epic.Id, epic.Slug, epic.Branch, epic.State, into, tip, ahead, behind, batchEpic,
            runs.Count, latest?.RunId, latest?.Summary?.ExitCode,
            outcomes.Where(o => o.State == TaskStates.Landed).Select(o => o.Task).Order(StringComparer.Ordinal).ToList(), open,
            worktrees.Count, unmerged.Count, running, upstream, blockers, blockers.Count == 0);
    }

    // Why a task worktree blocks a close, as a detail suffix ("" for plain unmerged work), or null when it does not block.
    // Unknown state (Error), a stale registration whose directory still exists and uncommitted edits may hide work the
    // epic lacks, so all three block even when the branch itself reads as merged.
    static string? UnmergedReason(WorktreeEntry w) =>
        w.Error is not null ? $"; could not be assessed: {w.Error}"
        : w.Missing && w.DirectoryExists ? "; registration stale but directory exists, inspect it"
        : w.Dirty || (w.MergedVia is null && w.AheadOfBase > 0) ? ""
        : null;

    // batch records a land failure as "land conflict with the epic tip" with no files and keeps the real reason (for
    // example the squash lander's requireTicket failure) in GitOutput.
    static string? ReasonOf(ReturnedEntry? r) =>
        r is { Stage: ReturnStage.Land, Files.Count: 0, GitOutput.Length: > 0 } ? TextLines.OneLine(r.GitOutput) : r?.Reason;
}
