namespace Swarm.RunState;

/// <summary>One gated run: the single stdout line of <c>testgate run</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Label">Caller-chosen label.</param>
/// <param name="WaitMs">Time spent waiting for a slot.</param>
/// <param name="RunMs">Time the command ran.</param>
/// <param name="Slot">Slot index held.</param>
/// <param name="ExitCode">The command's exit code (-1 when killed).</param>
/// <param name="Reclaimed">True when the slot was taken over from a stale lock.</param>
/// <param name="Killed">True when the command's process tree was killed.</param>
/// <param name="AcquiredUtc">When the slot was acquired.</param>
/// <param name="ReleasedUtc">When the slot was released.</param>
public sealed record GateResult(int SchemaVersion, string Label, long WaitMs, long RunMs, int Slot, int ExitCode, bool Reclaimed, bool Killed, DateTime AcquiredUtc, DateTime ReleasedUtc);

/// <summary>Why a task came back (<see cref="ReturnedEntry.Kind"/>).</summary>
public static class ReturnKind
{
    /// <summary>Merge or land conflict.</summary>
    public const string Conflict = "conflict";

    /// <summary>The suite was red with this task (bisect blame).</summary>
    public const string Red = "red";

    /// <summary>The task's branch does not exist.</summary>
    public const string BadInput = "bad-input";

    /// <summary>A task it depends on was returned.</summary>
    public const string Dependency = "dependency";
}

/// <summary>Where a task came back (<see cref="ReturnedEntry.Stage"/>).</summary>
public static class ReturnStage
{
    /// <summary>Before any merge.</summary>
    public const string Preflight = "preflight";

    /// <summary>Merging into the integration worktree.</summary>
    public const string Merge = "merge";

    /// <summary>Landing on the epic branch.</summary>
    public const string Land = "land";

    /// <summary>Running the full suite.</summary>
    public const string Suite = "suite";
}

/// <summary>Automatic rebase status (<see cref="ReturnedEntry.Rebase"/>).</summary>
public static class RebaseState
{
    /// <summary>No rebase applies (red, bad input).</summary>
    public const string NotApplicable = "n/a";

    /// <summary>A rebase will be attempted after the current batch.</summary>
    public const string Pending = "pending";

    /// <summary>The copy ref rebased cleanly.</summary>
    public const string Clean = "clean";

    /// <summary>The copy ref conflicted again.</summary>
    public const string Conflict = "conflict";

    /// <summary>No rebase: attempts used up, or the task is part of a stack.</summary>
    public const string Skipped = "skipped";
}

/// <summary>Final status of a returned task (<see cref="ReturnedEntry.Final"/>).</summary>
public static class FinalState
{
    /// <summary>Not decided yet.</summary>
    public const string Pending = "pending";

    /// <summary>Rebased copy requeued for a later batch.</summary>
    public const string Requeued = "requeued";

    /// <summary>Rebased copy landed (counts as landed).</summary>
    public const string RebasedAndLanded = "rebased-and-landed";

    /// <summary>Back to the worker: conflict that rebase cannot fix.</summary>
    public const string NeedsWorker = "needs-worker";

    /// <summary>Back to the worker: suite red.</summary>
    public const string ReturnedRed = "returned-red";

    /// <summary>Back to the caller: branch missing.</summary>
    public const string ReturnedBadInput = "returned-bad-input";

    /// <summary>Back to the caller: depends on a returned task.</summary>
    public const string BlockedByDependency = "blocked-by-dependency";

    /// <summary>The rebased copy has nothing left to land (its change is already on the epic).</summary>
    public const string NoOpAfterRebase = "no-op-after-rebase";
}

/// <summary>One line of <c>returned.jsonl</c>: a full snapshot of a task's return record (the last line per task wins).</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Utc">When this snapshot was written.</param>
/// <param name="RunId">The batch run.</param>
/// <param name="Task">Task id.</param>
/// <param name="Branch">The worker's branch (never modified by the tool).</param>
/// <param name="Kind">A <see cref="ReturnKind"/> value.</param>
/// <param name="Stage">A <see cref="ReturnStage"/> value.</param>
/// <param name="Batch">Batch number (0 = preflight).</param>
/// <param name="ConflictingWith">Task ids whose changes overlap the conflicting files (empty = the epic tip).</param>
/// <param name="Files">Conflicting files.</param>
/// <param name="Reason">One-line reason for the worker.</param>
/// <param name="GitOutput">Git's merge or land output.</param>
/// <param name="Rebase">A <see cref="RebaseState"/> value.</param>
/// <param name="RebasedBranch">The copy ref (<c>rebased/&lt;epic&gt;/&lt;task&gt;</c>) when one exists.</param>
/// <param name="RebaseOutput">Git's rebase output.</param>
/// <param name="Final">A <see cref="FinalState"/> value.</param>
public sealed record ReturnedEntry(
    int SchemaVersion, DateTime Utc, string RunId, string Task, string Branch, string Kind, string Stage, int Batch,
    IReadOnlyList<string> ConflictingWith, IReadOnlyList<string> Files, string Reason, string GitOutput,
    string Rebase, string? RebasedBranch, string RebaseOutput, string Final);

/// <summary>One full-suite run inside a batch.</summary>
/// <param name="Gate">The gate result.</param>
/// <param name="LogFile">The suite's combined output.</param>
/// <param name="Tasks">Task ids in the tested state.</param>
public sealed record SuiteRecord(GateResult Gate, string LogFile, IReadOnlyList<string> Tasks);

/// <summary>A task that landed on the epic branch.</summary>
/// <param name="Id">Task id.</param>
/// <param name="Batch">Batch number.</param>
/// <param name="Commit">The epic commit that contains it.</param>
/// <param name="Branch">The branch that was landed (worker branch or rebased copy).</param>
public sealed record LandedRecord(string Id, int Batch, string Commit, string Branch);

/// <summary>One tested set (a batch or a bisect half).</summary>
/// <param name="Batch">Batch number.</param>
/// <param name="Bisect">True for a bisect half.</param>
/// <param name="Tasks">Task ids merged into the tested state.</param>
/// <param name="Result">green, red, red (inferred), or a no-suite note.</param>
public sealed record BatchLogEntry(int Batch, bool Bisect, IReadOnlyList<string> Tasks, string Result);

/// <summary><c>summary.json</c> and the single stdout line of <c>batch run</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="RunId">Run id.</param>
/// <param name="Epic">Epic id.</param>
/// <param name="EpicBranch">Epic branch.</param>
/// <param name="Mode">batched or serial.</param>
/// <param name="Lander">The lander's name.</param>
/// <param name="ExitCode">The process exit code for this run.</param>
/// <param name="Note">Why the run stopped early, or a note (e.g. "empty batch").</param>
/// <param name="Tasks">Tasks in the file.</param>
/// <param name="TasksLanded">Tasks landed (including after rebase).</param>
/// <param name="Returned">Tasks returned and not landed.</param>
/// <param name="RebasedAndLanded">Tasks landed through a rebased copy.</param>
/// <param name="NeedsWorker">Tasks whose final state is needs-worker.</param>
/// <param name="RejectedRed">Tasks returned red.</param>
/// <param name="BadInput">Tasks returned for a missing branch or a returned dependency.</param>
/// <param name="Unprocessed">Tasks neither landed nor returned (the run stopped early).</param>
/// <param name="FullSuiteRuns">Suite runs, including bisect runs.</param>
/// <param name="BisectRuns">Suite runs on bisect halves.</param>
/// <param name="InferredRedSkipped">Suite runs skipped because the result was inferred red.</param>
/// <param name="Batches">Top-level batches.</param>
/// <param name="SizeTrace">Batch size used for each batch.</param>
/// <param name="WallSeconds">Wall time.</param>
/// <param name="WaitMs">Total slot wait.</param>
/// <param name="RunMs">Total suite run time.</param>
/// <param name="Suites">Every suite run.</param>
/// <param name="Landed">Every landed task.</param>
/// <param name="BatchLog">Every tested set.</param>
/// <param name="DerivedTouches">Files each task changes, derived from git at the start of the run.</param>
/// <param name="ReturnedFile">Path of <c>returned.jsonl</c>.</param>
/// <param name="EventsFile">Path of <c>events.jsonl</c>.</param>
public sealed record BatchSummary(
    int SchemaVersion, string RunId, string Epic, string EpicBranch, string Mode, string Lander, int ExitCode, string? Note,
    int Tasks, int TasksLanded, int Returned, int RebasedAndLanded, int NeedsWorker, int RejectedRed, int BadInput, IReadOnlyList<string> Unprocessed,
    int FullSuiteRuns, int BisectRuns, int InferredRedSkipped, int Batches, IReadOnlyList<int> SizeTrace,
    double WallSeconds, long WaitMs, long RunMs,
    IReadOnlyList<SuiteRecord> Suites, IReadOnlyList<LandedRecord> Landed, IReadOnlyList<BatchLogEntry> BatchLog,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DerivedTouches, string ReturnedFile, string EventsFile);
