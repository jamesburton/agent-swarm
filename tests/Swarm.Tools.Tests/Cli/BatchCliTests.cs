using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using BatchProgram = Swarm.Batch.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class BatchCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = BatchProgram.Run(args, stdout, stderr, repo.Root, new FastForwardLander());
        return (code, stdout.ToString(), stderr.ToString());
    }

    static TempRepo RepoWithTask(string file = "one.txt", string content = "1\n")
    {
        var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", (file, content));
        repo.WriteTasks(new TaskLine("T1", "task/T1"));
        return repo;
    }

    [Fact]
    public void Run_Green_PrintsSummaryAndExits0()
    {
        using var repo = RepoWithTask();
        var config = TestConfig.Write(repo, TestConfig.For(repo));
        var (code, output, _) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", config);
        Assert.Equal(0, code);
        var s = SingleJsonLine(output);
        Assert.Equal(1, s.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, s.GetProperty("tasksLanded").GetInt32());
        Assert.True(repo.HasFile("epic/E1", "one.txt"));
    }

    [Fact]
    public void Run_RedTask_Exits1AndStillPrintsSummary()
    {
        using var repo = RepoWithTask("T1.fail", "");
        var (code, output, _) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("rejectedRed").GetInt32());
    }

    [Fact]
    public void Run_MissingTasksFile_Exits3OneLine()
    {
        using var repo = RepoWithTask();
        var (code, output, err) = Run(repo, "run", "nope.json", "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Empty(output);
        Assert.StartsWith("error: tasks file '", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void Run_GateTimeout_Exits5WithSummaryAndErrorLine()
    {
        using var repo = RepoWithTask();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("other");
        var (code, output, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, config), "--max-wait", "1", "--verbosity", "quiet");
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Equal(5, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
        Assert.StartsWith("error: no test slot free after", err.TrimEnd());
    }

    [Theory]
    [InlineData("--mode", "bogus")]
    [InlineData("--experimental-fixed", "0")]
    [InlineData("--slots", "0")]
    public void Run_BadOptions_Exit2(string option, string value)
    {
        using var repo = RepoWithTask();
        var (code, _, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)), option, value);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.StartsWith("error: ", err.TrimEnd());
    }

    [Fact]
    public void Run_EpicFlagOverridesConfig()
    {
        using var repo = RepoWithTask();
        var (code, _, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)), "--epic", "E9");
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic branch 'epic/E9' not found", err);
    }
}
