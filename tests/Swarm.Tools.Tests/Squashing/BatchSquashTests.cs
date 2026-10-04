using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using static Swarm.Tools.Tests.Support.SquashFixture;
using BatchProgram = Swarm.Batch.Cli.Program;

namespace Swarm.Tools.Tests.Squashing;

public class BatchSquashTests
{
    static string[] TaskTrailers(TempRepo repo, string range) =>
        repo.Git("log", "--reverse", "--format=%(trailers:key=Task,valueonly,separator=%x2C)", range)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static (TempRepo Repo, string Before, string Tasks) FourTasks(Func<int, (string Path, string Content)>? fileFor = null)
    {
        var repo = TempRepo.Create();
        repo.Epic();
        for (var i = 1; i <= 4; i++)
        {
            repo.Branch($"task/T{i}", "epic/E1", fileFor?.Invoke(i) ?? ($"t{i}.txt", $"{i}\n"));
        }

        return (repo, repo.Sha("epic/E1"), repo.WriteTasks([.. Enumerable.Range(1, 4).Select(i => new TaskLine($"T{i}", $"task/T{i}"))]));
    }

    [Fact]
    public void Engine_AllGreen_OneSquashedCommitPerTaskNoMerges()
    {
        var (repo, before, tasks) = FourTasks();
        using var _ = repo;
        var s = BatchScenario.Run(repo, tasks, lander: new SquashLander(new SquashConfig(), "main"));
        Assert.Equal((ExitCodes.Ok, "squash", 4), (s.ExitCode, s.Lander, s.TasksLanded));
        Assert.Equal(new[] { "T1", "T2", "T3", "T4" }, TaskTrailers(repo, $"{before}..epic/E1"));
        Assert.Equal(0, Count(repo, $"{before}..epic/E1", "--merges"));
        Assert.Equal(4, s.Landed.Select(l => l.Commit).Distinct().Count());
        Assert.Equal("1", Trailers(repo, "epic/E1", "Batch"));
    }

    [Fact]
    public void Engine_RedTask_GreensLandSquashedInQueueOrder()
    {
        var (repo, before, tasks) = FourTasks(i => i == 3 ? ("T3.fail", "") : ($"t{i}.txt", $"{i}\n"));
        using var _ = repo;
        var s = BatchScenario.Run(repo, tasks, lander: new SquashLander(new SquashConfig(), "main"));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T4" }, TaskTrailers(repo, $"{before}..epic/E1"));
        Assert.False(repo.HasFile("epic/E1", "T3.fail"));
    }

    [Fact]
    public void Engine_LandFailure_ReturnsTaskAsNeedsWorkerAndLandsTheRest()
    {
        // T2 has no ticket and tickets are required: it fails at land, its rebased copy fails again, then needs a worker.
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/9933-c", "epic/E1", ("c.txt", "c\n"));
        var tasks = repo.WriteTasks(new TaskLine("T1", "task/9931-a"), new TaskLine("T2", "task/T2"), new TaskLine("T3", "task/9933-c"));
        var s = BatchScenario.Run(repo, tasks, lander: new SquashLander(new SquashConfig { RequireTicket = true }, "main"));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1", "T3" }, s.Landed.Select(l => l.Id));
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((ReturnKind.Conflict, ReturnStage.Land, FinalState.NeedsWorker), (t2.Kind, t2.Stage, t2.Final));
        Assert.Contains("no ticket", t2.GitOutput);
    }

    [Theory]
    [InlineData(LanderNames.Squash, typeof(SquashLander))]
    [InlineData(LanderNames.FastForward, typeof(FastForwardLander))]
    public void Landers_CreateFromConfig(string name, Type expected) =>
        Assert.IsType(expected, Landers.Create(new SwarmConfig { Lander = name }));

    [Fact]
    public void Landers_Unknown_IsUsage() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => Landers.Create(new SwarmConfig { Lander = "rebase" })).ExitCode);

    [Theory]
    [InlineData(LanderNames.Squash, 0)]
    [InlineData(LanderNames.FastForward, 1)]
    public void BatchCli_LanderComesFromConfig(string lander, int merges)
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        var before = repo.Sha("epic/E1");
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var tasks = repo.WriteTasks(new TaskLine("T1", "task/T1"));
        var config = TestConfig.Write(repo, TestConfig.For(repo) with { Lander = lander });
        var stdout = new StringWriter();
        var code = BatchProgram.Run(["run", tasks, "--config", config], stdout, new StringWriter(), repo.Root, Landers.Create);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(lander, SingleJsonLine(stdout.ToString()).GetProperty("lander").GetString());
        Assert.Equal(merges, Count(repo, $"{before}..epic/E1", "--merges"));
    }
}
