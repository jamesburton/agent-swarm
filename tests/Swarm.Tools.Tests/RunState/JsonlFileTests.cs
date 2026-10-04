using System.Text.Json;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class JsonlFileTests
{
    public sealed record Line(int Writer, int N);

    [Fact]
    public async Task ParallelAppends_AllLinesValid()
    {
        // Separate FileStreams per writer exercise the same OS sharing rules as separate processes.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "events.jsonl");
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                JsonlFile.Append(path, new Line(w, n));
            }
        }));
        await Task.WhenAll(writers);
        var lines = JsonlFile.ReadAll<Line>(path);
        Assert.Equal(400, lines.Count);
        Assert.Equal(400, lines.Distinct().Count());
    }

    [Fact]
    public void ReadAll_SkipsIncompleteLastLine()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "x.jsonl");
        JsonlFile.Append(path, new Line(1, 1));
        File.AppendAllText(path, "{\"writer\":2,");
        Assert.Single(JsonlFile.ReadAll<Line>(path));
    }

    [Fact]
    public void ReturnLedger_LastLinePerTaskWins()
    {
        using var dir = new TempDir();
        var ledger = new ReturnLedger(Path.Combine(dir.Dir, "returned.jsonl"), "run1");
        ledger.Record(ReturnLedger.New("T2", "task/T2", ReturnKind.Conflict, ReturnStage.Merge, 1, "merge conflict") with { Rebase = RebaseState.Pending });
        ledger.Update("T2", e => e with { Rebase = RebaseState.Clean, RebasedBranch = "rebased/E1/T2", Final = FinalState.Requeued });
        Assert.Equal(2, File.ReadAllLines(ledger.FilePath).Length);
        var latest = ReturnLedger.ReadLatest(ledger.FilePath);
        Assert.Equal(FinalState.Requeued, latest["T2"].Final);
        Assert.Equal("run1", latest["T2"].RunId);
        Assert.Equal(1, latest["T2"].SchemaVersion);
        Assert.Empty(ledger.Pending());
    }

    [Fact]
    public void EventLog_WritesRunEvents()
    {
        using var dir = new TempDir();
        var log = new EventLog(Path.Combine(dir.Dir, "events.jsonl"), "run1");
        log.Write(EventTypes.RunStart, new { tasks = 3 });
        var line = File.ReadAllLines(log.FilePath).Single();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("run-start", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("data").GetProperty("tasks").GetInt32());
    }
}
