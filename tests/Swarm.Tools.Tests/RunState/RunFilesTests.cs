using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class RunFilesTests
{
    static readonly DateTime T0 = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GateResult_IsCamelCaseWithSchemaVersionFirst() =>
        Assert.StartsWith("{\"schemaVersion\":1,\"label\":\"x\",\"waitMs\":", SwarmJson.Line(new GateResult(1, "x", 2, 3, 0, 0, false, false, T0, T0)));

    [Fact]
    public void WriteFile_IsLfWithTrailingNewline()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "sub", "summary.json");
        SwarmJson.WriteFile(path, new BatchLogEntry(1, false, ["T1"], "green"));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
        Assert.Equal("green", SwarmJson.Read<BatchLogEntry>(path).Result);
    }

    [Fact]
    public void NewRunId_IsSafe() => Assert.True(SafeName.IsValid(RunDirectories.NewRunId("E1", T0)));

    [Fact]
    public void Create_RejectsExistingOrUnsafeIds()
    {
        using var dir = new TempDir();
        var layout = new StateLayout(dir.Dir);
        RunDirectories.Create(layout, "r1");
        Assert.True(Directory.Exists(Path.Combine(layout.RunDir("r1"), "logs")));
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => RunDirectories.Create(layout, "r1")).ExitCode);
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => RunDirectories.Create(layout, "../x")).ExitCode);
    }

    [Fact]
    public void Prune_KeepsNewestFinishedRunsAndAllUnfinished()
    {
        using var dir = new TempDir();
        var layout = new StateLayout(dir.Dir);
        for (var i = 1; i <= 4; i++)
        {
            var run = RunDirectories.Create(layout, $"r{i}");
            var summary = Path.Combine(run, RunDirectories.SummaryFileName);
            File.WriteAllText(summary, "{}");
            File.SetLastWriteTimeUtc(summary, T0.AddMinutes(i));
        }

        RunDirectories.Create(layout, "crashed");
        var deleted = RunDirectories.Prune(layout, keep: 2);
        Assert.Equal(new[] { "r1", "r2" }, deleted.Order());
        Assert.True(Directory.Exists(layout.RunDir("crashed")));
        Assert.True(Directory.Exists(layout.RunDir("r4")));
    }

    [Fact]
    public void Progress_RespectsVerbosity()
    {
        var err = new StringWriter();
        var quiet = new Progress(err, Verbosity.Quiet);
        quiet.Info("info");
        quiet.Detail("detail");
        quiet.Warn("careful\nnow");
        Assert.Equal("warning: careful now", err.ToString().TrimEnd());
    }
}
