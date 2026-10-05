using System.Globalization;
using System.Text.Json;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Where a task stands across batch runs (<see cref="TaskOutcome.State"/>).</summary>
public static class TaskStates
{
    /// <summary>Landed on the epic.</summary>
    public const string Landed = "landed";

    /// <summary>Returned by its latest run.</summary>
    public const string Returned = "returned";

    /// <summary>Neither landed nor returned: its run stopped early.</summary>
    public const string Unprocessed = "unprocessed";
}

/// <summary>One batch run read from <c>&lt;state&gt;/runs/&lt;runId&gt;</c>.</summary>
/// <param name="RunId">Run id (folder name).</param>
/// <param name="StartedUtc">Time of the <c>run-start</c> event (folder creation time when absent).</param>
/// <param name="EpicBranch">Epic branch from the summary or the <c>run-start</c> event.</param>
/// <param name="Summary">The summary, or null when the run has none or it is unreadable.</param>
/// <param name="Returned">Latest <c>returned.jsonl</c> record per task.</param>
public sealed record RunRecord(string RunId, DateTime StartedUtc, string? EpicBranch, BatchSummary? Summary, IReadOnlyDictionary<string, ReturnedEntry> Returned)
{
    /// <summary>Gets a value indicating whether the run wrote a readable summary.</summary>
    public bool Finished => Summary is not null;
}

/// <summary>A task's latest state across runs.</summary>
/// <param name="Task">Task id.</param>
/// <param name="State">A <see cref="TaskStates"/> value.</param>
/// <param name="Branch">The worker's branch, when known.</param>
/// <param name="RunId">The run that decided the state.</param>
/// <param name="LastReturn">The latest return record from that run, if any.</param>
public sealed record TaskOutcome(string Task, string State, string Branch, string RunId, ReturnedEntry? LastReturn)
{
    /// <summary>Gets a value indicating whether the task blocks closing its epic.</summary>
    public bool Blocking => State == TaskStates.Unprocessed || (State == TaskStates.Returned && LastReturn?.Final != FinalState.NoOpAfterRebase);
}

/// <summary>Reads Plan A batch run state (read-only).</summary>
public sealed class RunHistory
{
    RunHistory(IReadOnlyList<RunRecord> runs) => Runs = runs;

    /// <summary>Gets every run, oldest first.</summary>
    public IReadOnlyList<RunRecord> Runs { get; }

    /// <summary>Loads every run under the state directory.</summary>
    /// <param name="layout">State layout.</param>
    /// <returns>The history (empty when there are no runs).</returns>
    public static RunHistory Load(StateLayout layout)
    {
        if (!Directory.Exists(layout.RunsDir))
        {
            return new RunHistory([]);
        }

        var runs = new List<RunRecord>();
        foreach (var dir in new DirectoryInfo(layout.RunsDir).GetDirectories())
        {
            var start = JsonlFile.ReadAll<RunEvent>(Path.Combine(dir.FullName, "events.jsonl")).FirstOrDefault(e => e.Type == EventTypes.RunStart);
            var summary = ReadSummary(Path.Combine(dir.FullName, RunDirectories.SummaryFileName));
            var epic = summary?.EpicBranch ?? (start?.Data is JsonElement { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("epicBranch", out var b) ? b.GetString() : null);
            runs.Add(new RunRecord(dir.Name, start?.Utc ?? dir.CreationTimeUtc, epic, summary, ReturnLedger.ReadLatest(Path.Combine(dir.FullName, "returned.jsonl"))));
        }

        return new RunHistory(runs.OrderBy(r => r.StartedUtc).ThenBy(r => r.RunId, StringComparer.Ordinal).ToList());
    }

    /// <summary>Folds runs (oldest first) into each task's latest outcome; within a run, landing wins over returning.</summary>
    /// <param name="runsInOrder">Runs, oldest first.</param>
    /// <returns>Task id to outcome.</returns>
    public static IReadOnlyDictionary<string, TaskOutcome> Outcomes(IEnumerable<RunRecord> runsInOrder)
    {
        var map = new Dictionary<string, TaskOutcome>(StringComparer.Ordinal);
        foreach (var run in runsInOrder)
        {
            foreach (var r in run.Returned.Values)
            {
                map[r.Task] = new TaskOutcome(r.Task, TaskStates.Returned, r.Branch, run.RunId, r);
            }

            if (run.Summary is not { } s)
            {
                continue;
            }

            foreach (var id in s.Unprocessed.Where(id => !run.Returned.ContainsKey(id)))
            {
                map[id] = new TaskOutcome(id, TaskStates.Unprocessed, map.GetValueOrDefault(id)?.Branch ?? "", run.RunId, null);
            }

            foreach (var l in s.Landed)
            {
                var r = run.Returned.GetValueOrDefault(l.Id);
                map[l.Id] = new TaskOutcome(l.Id, TaskStates.Landed, r?.Branch ?? l.Branch, run.RunId, r);
            }
        }

        return map;
    }

    /// <summary>Gets the runs of one epic branch.</summary>
    /// <param name="epicBranch">Epic branch name.</param>
    /// <returns>Matching runs, oldest first.</returns>
    public IReadOnlyList<RunRecord> ForEpic(string epicBranch) =>
        Runs.Where(r => string.Equals(r.EpicBranch, epicBranch, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Gets the branches that batch run state of one epic records as landed and whose work is still unchanged since.
    /// A branch is included only when it exists and its current tip's committer date is not after the start of the
    /// latest run (of that epic) that landed it; a branch that gained commits after landing, was deleted and re-created
    /// under the same name, never landed on this epic, or cannot be read from git is omitted, so callers fall through to
    /// the git-based checks of <see cref="MergeCheck.LandedVia"/>.
    /// </summary>
    /// <param name="git">Runner in any worktree of the repository.</param>
    /// <param name="epicBranch">Epic branch name; only its runs are considered.</param>
    /// <returns>Branch names (landed branches plus worker branches of rebased-and-landed tasks).</returns>
    public IReadOnlySet<string> LandedBranches(GitRunner git, string epicBranch)
    {
        var landedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var run in ForEpic(epicBranch))
        {
            foreach (var branch in (run.Summary?.Landed.Select(l => l.Branch) ?? [])
                .Concat(run.Returned.Values.Where(r => r.Final == FinalState.RebasedAndLanded).Select(r => r.Branch)))
            {
                // Runs are oldest first, so the last write is the latest landing run.
                landedAt[branch] = run.StartedUtc;
            }
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (branch, started) in landedAt)
        {
            var tip = git.Try("log", "-1", "--format=%cI", GitRunner.HeadsRef(branch));
            if (tip.ExitCode == 0
                && DateTimeOffset.TryParse(tip.StdOut.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var committed)
                && committed.UtcDateTime <= started)
            {
                set.Add(branch);
            }
        }

        return set;
    }

    static BatchSummary? ReadSummary(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return SwarmJson.Read<BatchSummary>(path);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
        {
            // Unreadable (torn or hand-edited): treated as unfinished, which blocks a close until someone looks.
            return null;
        }
    }
}
