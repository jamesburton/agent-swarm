using System.Diagnostics;
using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Batching;

/// <summary>Batch modes.</summary>
public static class BatchModes
{
    /// <summary>Adaptive batches with pre-batching and bisect.</summary>
    public const string Batched = "batched";

    /// <summary>One task per suite: the baseline.</summary>
    public const string Serial = "serial";
}

/// <summary>Options for one batch run.</summary>
public sealed record BatchRunOptions
{
    /// <summary>Gets the absolute tasks file path.</summary>
    public required string TasksFile { get; init; }

    /// <summary>Gets the validated config (flags already applied).</summary>
    public required SwarmConfig Config { get; init; }

    /// <summary>Gets an explicit run id, or null for a timestamped one.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the mode (<see cref="BatchModes"/>).</summary>
    public string Mode { get; init; } = BatchModes.Batched;

    /// <summary>Gets a pre-batching override (null = config).</summary>
    public bool? Prebatch { get; init; }

    /// <summary>Gets an experimental fixed batch size (start = min = max), or null.</summary>
    public int? FixedSize { get; init; }
}

/// <summary>
/// Integrates task branches onto the epic in adaptive batches: sequential merge with stop-on-conflict, one full
/// suite per batch under a test slot, halving bisect on red, landing through an <see cref="ILander"/> on green,
/// and an automatic rebase of a copy ref for conflicting tasks.
/// </summary>
public sealed class BatchEngine
{
    readonly RepoPaths repo;
    readonly BatchRunOptions options;
    readonly SwarmConfig config;
    readonly ILander lander;
    readonly Progress progress;
    readonly GitRunner main;
    readonly StateLayout state;
    readonly string epicBranch;
    readonly TouchIndex touches;
    readonly Dictionary<string, TaskSpec> original = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> position = new(StringComparer.Ordinal);

    // Units going back to the queue front after the current top-level batch (requeued and rebased copies).
    readonly List<TaskUnit> front = [];
    readonly List<string> landedIds = [];
    readonly List<LandedRecord> landed = [];
    readonly List<SuiteRecord> suites = [];
    readonly List<BatchLogEntry> batchLog = [];
    readonly List<int> sizeTrace = [];
    readonly Dictionary<string, int> rebaseAttempts = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, IReadOnlyList<string>> initialTouches = new Dictionary<string, IReadOnlyList<string>>();

    // Set in Run before first use (the engine runs once); null! keeps nullable flow analysis quiet.
    IntegrationWorktree integration = null!;
    GateRunner gate = null!;
    ReturnLedger ledger = null!;
    EventLog events = null!;
    string runId = "";
    string runDir = "";
    string lastSuiteLog = "";
    int suiteRuns;
    int bisectRuns;
    int inferred;
    int batchNo;
    long waitMs;
    long runMs;
    bool started;

    /// <summary>Initializes a new instance of the <see cref="BatchEngine"/> class.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="options">Run options.</param>
    /// <param name="lander">How green tasks land on the epic.</param>
    /// <param name="progress">Human progress (stderr).</param>
    /// <exception cref="ToolException">The state dir path is invalid (exit code 2).</exception>
    public BatchEngine(RepoPaths repo, BatchRunOptions options, ILander lander, Progress progress)
    {
        this.repo = repo;
        this.options = options;
        config = options.Config;
        this.lander = lander;
        this.progress = progress;
        main = new GitRunner(repo.MainWorktreeRoot);
        state = new StateLayout(StatePaths.Resolve(repo, config.StateDir));
        epicBranch = config.EpicBranch;
        touches = new TouchIndex(main);
    }

    sealed record SetOutcome(bool Red, bool CleanAndLanded);

    /// <summary>Runs the batch (once per engine).</summary>
    /// <param name="cancellationToken">Stops the run; the summary records exit 4.</param>
    /// <returns>The summary (also written to <c>summary.json</c>).</returns>
    /// <exception cref="ToolException">Pre-run failure: bad mode/size or path (2), tasks file or epic (3), concurrent run (4).
    /// Once the run directory exists, every failure (including unexpected exceptions) is recorded in the summary instead.</exception>
    public BatchSummary Run(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            throw new InvalidOperationException("a BatchEngine runs once");
        }

        started = true;
        var wall = Stopwatch.StartNew();

        // Pre-run checks: each failure throws before anything is created.
        if (options.Mode is not (BatchModes.Batched or BatchModes.Serial))
        {
            throw new ToolException(ExitCodes.Usage, $"unknown mode '{options.Mode}' (batched|serial)");
        }

        if (options.FixedSize is < 1 or > 64)
        {
            throw new ToolException(ExitCodes.Usage, $"fixed batch size must be between 1 and 64 (got {options.FixedSize})");
        }

        var integrationPath = StatePaths.Guard(Path.Combine(StatePaths.ResolveWorktreeRoot(repo, config.WorktreeRoot), "int-" + config.Epic), "integration worktree");
        var tasks = TasksFile.Load(options.TasksFile);
        EnsureNoCopyNameClash(tasks);
        RepoChecks.EnsureEpic(main, epicBranch);
        runId = options.RunId ?? RunDirectories.NewRunId(config.Epic, DateTime.UtcNow);
        var lockOptions = SlotOptions.From(config) with { Slots = 1, MaxWait = null };
        using var batchLock = new SlotSemaphore(state.BatchLockDir(config.Epic), lockOptions).TryAcquire($"batch run {runId}")
            ?? throw new ToolException(ExitCodes.Environment, $"another batch run holds epic '{config.Epic}'", $"wait for it to finish; lock in {state.BatchLockDir(config.Epic)}");

        RunDirectories.Prune(state, config.KeepRuns);
        runDir = RunDirectories.Create(state, runId);
        events = new EventLog(Path.Combine(runDir, "events.jsonl"), runId);
        ledger = new ReturnLedger(Path.Combine(runDir, "returned.jsonl"), runId);
        foreach (var t in tasks)
        {
            position[t.Id] = original.Count;
            original[t.Id] = t;
        }

        events.Write(EventTypes.RunStart, new { tasks = tasks.Count, epic = config.Epic, epicBranch, mode = options.Mode, lander = lander.Name });
        if (tasks.Count == 0)
        {
            progress.Info("nothing to do: the tasks file is empty (0 tasks); no worktree created, no suite run");
            return Finish(wall, null, "empty batch");
        }

        try
        {
            Loop(Preflight(tasks), integrationPath, cancellationToken);
            return Finish(wall, null, null);
        }
        catch (ToolException e)
        {
            return Finish(wall, e.ExitCode, e.Summary);
        }
        catch (OperationCanceledException)
        {
            return Finish(wall, ExitCodes.Environment, "cancelled");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A lander bug, a log-write IOException, a process-kill AggregateException: the run still ends with a
            // summary, so it is never left unfinished (retention and run history depend on summary.json).
            return Finish(wall, ExitCodes.Environment, $"unexpected failure: {e.GetType().Name}: {TextLines.OneLine(e.Message)}");
        }
    }

    static string Ids(IEnumerable<TaskUnit> units) => string.Join(',', units.SelectMany(u => u.Ids));

    IReadOnlyList<TaskSpec> Preflight(IReadOnlyList<TaskSpec> tasks)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<TaskSpec>();
        foreach (var t in tasks)
        {
            if (!main.RefExists(t.BranchRef))
            {
                dropped.Add(t.Id);
                progress.Info($"  RETURN {t.Id}: branch '{t.Branch}' not found");
                ledger!.Record(Entry(t, ReturnKind.BadInput, ReturnStage.Preflight, 0, $"task branch '{t.Branch}' not found") with { Final = FinalState.ReturnedBadInput });
                continue;
            }

            if (t.DependsOn.FirstOrDefault(dropped.Contains) is { } dep)
            {
                dropped.Add(t.Id);
                progress.Info($"  RETURN {t.Id}: depends on returned task {dep}");
                ledger!.Record(Entry(t, ReturnKind.Dependency, ReturnStage.Preflight, 0, $"depends on returned task '{dep}'") with { ConflictingWith = [dep], Final = FinalState.BlockedByDependency });
                continue;
            }

            ready.Add(t);
        }

        return ready;
    }

    void Loop(IReadOnlyList<TaskSpec> ready, string integrationPath, CancellationToken ct)
    {
        var queue = TaskUnits.Build(ready).ToList();
        if (queue.Count == 0)
        {
            return;
        }

        integration = new IntegrationWorktree(main, integrationPath);
        integration.Ensure(epicBranch);
        gate = new GateRunner(new SlotSemaphore(state.SlotsDir, SlotOptions.From(config)), line => progress.Detail("    " + line));
        var tip = EpicTip();
        foreach (var t in ready)
        {
            touches.Set(t.Id, touches.Derive(tip, t.BranchRef));
        }

        initialTouches = touches.Snapshot();
        var (start, min, max) = options.Mode == BatchModes.Serial ? (1, 1, 1)
            : options.FixedSize is { } n ? (n, n, n)
            : (config.Batch.Start, config.Batch.Min, config.Batch.Max);
        var prebatch = options.Mode != BatchModes.Serial && (options.Prebatch ?? config.Prebatch);
        var size = start;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var pick = BatchPlanner.PreBatch(queue, size, touches.Overlaps, prebatch);
            foreach (var unit in pick)
            {
                queue.Remove(unit);
            }

            batchNo++;
            sizeTrace.Add(size);
            progress.Info($"batch {batchNo} size {size}: {Ids(pick)}");
            events!.Write(EventTypes.BatchStart, new { batch = batchNo, size, tasks = pick.SelectMany(u => u.Ids).ToList() });
            var outcome = ProcessSet(pick, batchNo, knownRed: false, bisect: false, ct);
            RebaseReturned();

            // Requeued units go back as one block in tasks-file order, so "the later task is blamed" still holds.
            queue.InsertRange(0, front.OrderBy(u => u.Members.Min(m => position[m.Id])));
            front.Clear();
            size = BatchPlanner.NextSize(size, outcome.Red, min, max);
        }

        integration.ResetTo(GitRunner.HeadsRef(epicBranch));
    }

    SetOutcome ProcessSet(IReadOnlyList<TaskUnit> units, int batch, bool knownRed, bool bisect, CancellationToken ct)
    {
        var tip = EpicTip();
        var integ = integration!.Integrate(tip, units);
        var mergedIds = integ.Merged.SelectMany(u => u.Ids).ToList();
        events!.Write(EventTypes.Merge, new { batch, merged = mergedIds, conflicts = integ.Conflicts.Select(c => c.Offender.Id).ToList(), head = integ.Head });
        foreach (var c in integ.Conflicts)
        {
            ReturnConflict(c, batch, ReturnStage.Merge);
        }

        if (integ.Merged.Count == 0)
        {
            batchLog.Add(new BatchLogEntry(batch, bisect, mergedIds, "all merges conflicted: no suite run"));
            return new SetOutcome(Red: false, CleanAndLanded: false);
        }

        bool red;
        if (knownRed)
        {
            red = true;
            inferred++;
        }
        else
        {
            red = Suite(batch, bisect, mergedIds, integ.Head, ct) != 0;
        }

        batchLog.Add(new BatchLogEntry(batch, bisect, mergedIds, knownRed ? "red (inferred)" : red ? "red" : "green"));
        if (!red)
        {
            return new SetOutcome(false, LandSet(integ, tip, batch) && integ.Conflicts.Count == 0);
        }

        if (integ.Merged.Count == 1)
        {
            RejectRed(integ.Merged[0], batch);
            return new SetOutcome(true, false);
        }

        var (left, right) = BatchPlanner.Halve(integ.Merged);
        events.Write(EventTypes.Bisect, new { batch, left = left.SelectMany(u => u.Ids).ToList(), right = right.SelectMany(u => u.Ids).ToList() });
        var l = ProcessSet(left, batch, knownRed: false, bisect: true, ct);

        // Left green and fully landed: epic tip + right is exactly the state already seen red, so infer instead of re-running.
        ProcessSet(right, batch, knownRed: !l.Red && l.CleanAndLanded, bisect: true, ct);
        return new SetOutcome(true, false);
    }

    int Suite(int batch, bool bisect, IReadOnlyList<string> ids, string head, CancellationToken ct)
    {
        suiteRuns++;
        if (bisect)
        {
            bisectRuns++;
        }

        var label = $"b{batch}-{(bisect ? "bisect" : "batch")}-{suiteRuns:000}";
        var run = gate!.Run(new GateRequest(config.TestCommand, integration!.WorktreePath, label), ct);
        lastSuiteLog = Path.Combine(runDir, "logs", $"suite-{suiteRuns:000}.log");
        File.WriteAllText(lastSuiteLog, run.Log);
        suites.Add(new SuiteRecord(run.Result, lastSuiteLog, ids));
        waitMs += run.Result.WaitMs;
        runMs += run.Result.RunMs;
        var verdict = run.Result.ExitCode == 0 ? "green" : "red";
        events!.Write(EventTypes.Suite, new { label, tasks = ids, head, verdict, run.Result.ExitCode, run.Result.WaitMs, run.Result.RunMs, log = lastSuiteLog });
        progress.Info($"  suite {label} [{string.Join(',', ids)}]: {verdict} ({run.Result.RunMs} ms, waited {run.Result.WaitMs} ms)");
        return run.Result.ExitCode;
    }

    bool LandSet(IntegrationResult integ, string tip, int batch)
    {
        var members = integ.Merged.SelectMany(u => u.Members).ToList();
        var byId = members.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var request = new LandRequest(main, integration!.Git, config.Epic, epicBranch, tip, integ.Head, members.Select(t => new LandTask(t.Id, t.Branch, t.DependsOn)).ToList(), batch, runId);
        var result = lander.Land(request);
        Verify(request, result);
        foreach (var l in result.Landed)
        {
            MarkLanded(byId[l.TaskId], batch, l.Commit);
        }

        var returnedHere = new HashSet<string>(StringComparer.Ordinal);
        if (result.Failure is { } failure)
        {
            // The failed task's unrelanded stack mates go back with it (a stack lands as one unit).
            var unit = integ.Merged.First(u => u.Ids.Contains(failure.TaskId));
            var rest = unit.Members.Where(m => !landedIds.Contains(m.Id)).ToList();
            ReturnConflict(new UnitConflict(new TaskUnit(rest), byId[failure.TaskId], failure.Files, failure.GitOutput, landedIds.ToList()), batch, ReturnStage.Land);
            returnedHere.UnionWith(rest.Select(m => m.Id));
        }

        var requeue = members.Where(t => result.NotAttempted.Contains(t.Id) && !returnedHere.Contains(t.Id)).ToList();
        if (requeue.Count > 0)
        {
            front.AddRange(TaskUnits.Build(requeue));
            events!.Write(EventTypes.Requeue, new { batch, tasks = requeue.Select(t => t.Id).ToList(), reason = "not attempted after a land failure" });
            progress.Info($"  REQUEUE {string.Join(',', requeue.Select(t => t.Id))}: not attempted after a land failure; retested next batch");
        }

        return result.Failure is null && result.NotAttempted.Count == 0;
    }

    void Verify(LandRequest request, LandResult result)
    {
        var actual = EpicTip();
        if (!string.Equals(actual, result.EpicTipAfter, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' reported epic tip {result.EpicTipAfter} but '{epicBranch}' is at {actual}");
        }

        var accounted = result.Landed.Select(l => l.TaskId).Concat(result.Failure is { } f ? new[] { f.TaskId } : Array.Empty<string>()).Concat(result.NotAttempted).Order(StringComparer.Ordinal);
        if (!accounted.SequenceEqual(request.Tasks.Select(t => t.Id).Order(StringComparer.Ordinal)))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' did not account for every task of batch {request.Batch} exactly once");
        }

        // NotAttempted only ever follows a failure; otherwise the tasks would be requeued and retested forever.
        if (result.Failure is null && result.NotAttempted.Count > 0)
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' reported not-attempted tasks without a failure in batch {request.Batch}");
        }

        if (result.Failure is null && result.NotAttempted.Count == 0
            && main.Run("rev-parse", result.EpicTipAfter + "^{tree}") != main.Run("rev-parse", request.TestedCommit + "^{tree}"))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' landed a tree that differs from the tested tree ('{epicBranch}' is at {actual})", "the epic now holds untested content: inspect it before the next run");
        }
    }

    void MarkLanded(TaskSpec t, int batch, string commit)
    {
        landedIds.Add(t.Id);
        landed.Add(new LandedRecord(t.Id, batch, commit, t.Branch));
        events!.Write(EventTypes.Land, new { task = t.Id, batch, commit, branch = t.Branch });
        if (ledger!.Get(t.Id) is { Final: FinalState.Requeued })
        {
            ledger.Update(t.Id, e => e with { Final = FinalState.RebasedAndLanded });
        }
    }

    void RejectRed(TaskUnit unit, int batch)
    {
        foreach (var t in unit.Members)
        {
            progress.Info($"  REJECT {t.Id}: suite red with this task on the epic tip");
            var reason = unit.Size == 1
                ? $"suite red with this task on the epic tip (culprit isolated by bisect; later task of a pair is blamed; log {lastSuiteLog})"
                : $"suite red for stack {string.Join('+', unit.Ids)} (a stack is tested as one unit; log {lastSuiteLog})";
            ledger!.Record(Entry(t, ReturnKind.Red, ReturnStage.Suite, batch, reason) with { Final = FinalState.ReturnedRed });
        }
    }

    void ReturnConflict(UnitConflict c, int batch, string stage)
    {
        var partners = touches.Partners(c.MergedBefore.Concat(landedIds).Where(id => !c.Unit.Ids.Contains(id)), c.Files);
        var against = partners.Count > 0 ? string.Join('+', partners) : "the epic tip";
        progress.Info($"  CONFLICT {c.Offender.Id} vs {against} in {string.Join(',', c.Files)} ({stage}): returned to worker");
        events!.Write(EventTypes.Conflict, new { task = c.Offender.Id, stage, batch, files = c.Files, conflictingWith = partners });
        var single = c.Unit.Size == 1;
        foreach (var t in c.Unit.Members)
        {
            var offender = t.Id == c.Offender.Id;
            var reason = offender ? $"{stage} conflict with {against}" : $"stack member '{c.Offender.Id}' conflicted ({stage}); a stack returns as one unit";
            ledger!.Record(Entry(t, ReturnKind.Conflict, stage, batch, reason) with
            {
                ConflictingWith = partners,
                Files = offender ? c.Files : Array.Empty<string>(),
                GitOutput = offender ? c.GitOutput : "",
                Rebase = single ? RebaseState.Pending : RebaseState.Skipped,
                Final = single ? FinalState.Pending : FinalState.NeedsWorker,
            });
        }
    }

    // After each top-level batch: rebase a COPY of each conflicting task onto the epic tip; clean => requeue at the front.
    // The copy is always rebuilt from the ORIGINAL worker branch (never from an earlier copy), so the worker branch is
    // never moved or deleted (R9) and a requeued copy is never both the source and the target of a rebase. Copy names
    // never equal a worker branch (EnsureNoCopyNameClash).
    void RebaseReturned()
    {
        foreach (var entry in ledger!.Pending())
        {
            var task = original[entry.Task];
            var copy = CopyBranch(task.Id);
            var attempts = rebaseAttempts.GetValueOrDefault(task.Id);
            if (attempts >= config.MaxRebaseAttempts)
            {
                ledger.Record(entry with { Rebase = RebaseState.Skipped, Final = FinalState.NeedsWorker, Reason = $"{entry.Reason} (automatic rebases used: {attempts} of {config.MaxRebaseAttempts})" });
                continue;
            }

            rebaseAttempts[task.Id] = attempts + 1;
            var tip = EpicTip();
            var outcome = integration!.RebaseCopy(task, tip, copy);
            events!.Write(EventTypes.Rebase, new { task = task.Id, copy, clean = outcome.Clean });
            if (!outcome.Clean)
            {
                // RebaseCopy deleted the copy ref, so the record must not point at it.
                progress.Info($"  REBASE {task.Id}: conflicts again -> needs-worker");
                ledger.Record(entry with { Rebase = RebaseState.Conflict, RebasedBranch = null, RebaseOutput = outcome.Output, Final = FinalState.NeedsWorker });
                continue;
            }

            var files = touches.Derive(tip, GitRunner.HeadsRef(copy));
            if (files.Count == 0)
            {
                ledger.Record(entry with { Rebase = RebaseState.Clean, RebasedBranch = copy, RebaseOutput = outcome.Output, Final = FinalState.NoOpAfterRebase });
                continue;
            }

            touches.Set(task.Id, files);
            front.Add(new TaskUnit([task with { Branch = copy }]));
            progress.Info($"  REBASE {task.Id}: clean -> requeued as {copy}");
            ledger.Record(entry with { Rebase = RebaseState.Clean, RebasedBranch = copy, RebaseOutput = outcome.Output, Final = FinalState.Requeued });
        }
    }

    string EpicTip() => main.RevParse(GitRunner.HeadsRef(epicBranch));

    string CopyBranch(string taskId) => $"rebased/{config.Epic}/{taskId}";

    // A worker branch named like any task's rebase copy would be reset (checkout -B) or deleted (branch -D) by that
    // task's automatic rebase, so such a tasks file is rejected before anything is created.
    void EnsureNoCopyNameClash(IReadOnlyList<TaskSpec> tasks)
    {
        var copies = tasks.Select(t => CopyBranch(t.Id)).ToHashSet(StringComparer.Ordinal);
        if (tasks.FirstOrDefault(t => copies.Contains(t.Branch)) is { } clash)
        {
            throw new ToolException(ExitCodes.BadInput, $"task '{clash.Id}': branch '{clash.Branch}' is reserved for the batch tool's rebase copies", "rename the task branch (rebased/<epic>/<task id> is tool-owned)");
        }
    }

    ReturnedEntry Entry(TaskSpec t, string kind, string stage, int batch, string reason)
    {
        var workerBranch = original[t.Id].Branch;
        return ReturnLedger.New(t.Id, workerBranch, kind, stage, batch, reason) with { RebasedBranch = t.Branch == workerBranch ? null : t.Branch };
    }

    BatchSummary Finish(Stopwatch wall, int? exitOverride, string? note)
    {
        var latest = ledger!.Latest;
        var returnedOpen = latest.Count(e => !landedIds.Contains(e.Task));
        var unprocessed = original.Keys.Where(id => !landedIds.Contains(id) && ledger.Get(id) is null).ToList();
        var exit = exitOverride ?? (landedIds.Count == original.Count ? ExitCodes.Ok : ExitCodes.Returned);
        var summary = new BatchSummary(
            SwarmJson.SchemaVersion, runId, config.Epic, epicBranch, options.Mode, lander.Name, exit, note,
            original.Count, landedIds.Count, returnedOpen,
            latest.Count(e => e.Final == FinalState.RebasedAndLanded),
            latest.Count(e => e.Final == FinalState.NeedsWorker),
            latest.Count(e => e.Final == FinalState.ReturnedRed),
            latest.Count(e => e.Final is FinalState.ReturnedBadInput or FinalState.BlockedByDependency),
            unprocessed, suiteRuns, bisectRuns, inferred, batchNo, sizeTrace,
            Math.Round(wall.Elapsed.TotalSeconds, 1), waitMs, runMs, suites, landed, batchLog, initialTouches,
            ledger.FilePath, events!.FilePath);
        SwarmJson.WriteFile(Path.Combine(runDir, RunDirectories.SummaryFileName), summary);
        events.Write(EventTypes.RunEnd, new { exitCode = exit, landed = landedIds.Count, returned = returnedOpen, note });
        progress.Info($"done: {landedIds.Count} of {original.Count} landed, {returnedOpen} returned, {suiteRuns} suite runs (exit {exit})");
        return summary;
    }
}
