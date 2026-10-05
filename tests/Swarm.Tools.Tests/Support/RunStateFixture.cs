using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

/// <summary>Writes run-state files in Plan A's formats, as batch would.</summary>
public static class RunStateFixture
{
    public static ReturnedEntry Returned(string task, string branch, string final, string kind = ReturnKind.Red) =>
        ReturnLedger.New(task, branch, kind, ReturnStage.Suite, 1, "test") with { Final = final };

    public static string WriteRun(
        string stateDir, string runId, DateTime startedUtc, string epicBranch, IReadOnlyList<LandedRecord> landed,
        IReadOnlyList<ReturnedEntry> returned, IReadOnlyList<string>? unprocessed = null, bool finished = true, int exitCode = -1)
    {
        var dir = new StateLayout(stateDir).RunDir(runId);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var events = Path.Combine(dir, "events.jsonl");
        var returnedFile = Path.Combine(dir, "returned.jsonl");
        // The same run-start payload shape as BatchEngine.Run.
        JsonlFile.Append(events, new RunEvent(1, startedUtc, runId, EventTypes.RunStart, new { tasks = landed.Count + returned.Count, epic = "E1", epicBranch, mode = "batched", lander = "squash" }));
        foreach (var r in returned)
        {
            JsonlFile.Append(returnedFile, r with { RunId = runId, Utc = startedUtc });
        }

        if (finished)
        {
            unprocessed ??= [];
            var exit = exitCode >= 0 ? exitCode : returned.Count + unprocessed.Count == 0 ? 0 : 1;
            var summary = new BatchSummary(
                1, runId, "E1", epicBranch, "batched", "squash", exit, null,
                landed.Count + returned.Count + unprocessed.Count, landed.Count, returned.Count, 0, 0, 0, 0, unprocessed,
                1, 0, 0, 1, [4], 1.0, 0, 0, [], landed, [], new Dictionary<string, IReadOnlyList<string>>(), returnedFile, events);
            SwarmJson.WriteFile(Path.Combine(dir, RunDirectories.SummaryFileName), summary);
        }

        return dir;
    }
}
