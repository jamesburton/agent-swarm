using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class BatchEngineTests
{
    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        return repo;
    }

    static string FourTasks(TempRepo repo, Func<int, (string Path, string Content)>? fileFor = null)
    {
        for (var i = 1; i <= 4; i++)
        {
            repo.Branch($"task/T{i}", "epic/E1", fileFor?.Invoke(i) ?? ($"t{i}.txt", $"{i}\n"));
        }

        return repo.WriteTasks([.. Enumerable.Range(1, 4).Select(i => new TaskLine($"T{i}", $"task/T{i}"))]);
    }

    [Fact]
    public void AllGreen_LandsEverythingInOneSuite_Exit0()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.Equal(4, s.TasksLanded);
        Assert.Equal(1, s.FullSuiteRuns);
        Assert.Equal(new[] { 4 }, s.SizeTrace);
        Assert.All(Enumerable.Range(1, 4), i => Assert.True(repo.HasFile("epic/E1", $"t{i}.txt")));
        Assert.True(File.Exists(Path.Combine(repo.StateDir, "runs", s.RunId, "summary.json")));
        Assert.Contains("\"type\":\"run-end\"", File.ReadAllText(s.EventsFile));
    }

    [Fact]
    public void RedTask_IsBisectedWithInferenceAndReturned_Exit1()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo, i => i == 3 ? ("T3.fail", "") : ($"t{i}.txt", $"{i}\n")));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T4" }, s.Landed.Select(l => l.Id));
        Assert.Equal((4, 3, 1), (s.FullSuiteRuns, s.BisectRuns, s.InferredRedSkipped));
        Assert.Equal(FinalState.ReturnedRed, BatchScenario.Returned(s)["T3"].Final);
        Assert.False(repo.HasFile("epic/E1", "T3.fail"));
    }

    [Fact]
    public void InteractionPair_BlamesTheLaterTask()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo, i => i switch { 1 => ("pair.fail", "t3.txt\n"), _ => ($"t{i}.txt", $"{i}\n") });
        var s = BatchScenario.Run(repo, tasks);
        Assert.Equal(new[] { "T1", "T2", "T4" }, s.Landed.Select(l => l.Id));
        Assert.Equal(ReturnKind.Red, BatchScenario.Returned(s)["T3"].Kind);
    }

    [Fact]
    public void Stack_LandsAsOneUnitAndIsNeverSplit()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("s1.txt", "1\n"));
        repo.Branch("task/T2", "task/T1", ("s2.txt", "2\n"));
        repo.Branch("task/T3", "epic/E1", ("T3.fail", ""));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2", ["T1"]), new TaskLine("T3", "task/T3")));
        Assert.Equal(new[] { "T1", "T2" }, s.Landed.Select(l => l.Id));
        Assert.Single(s.Landed.Select(l => l.Batch).Distinct());
        Assert.Equal((2, 1), (s.FullSuiteRuns, s.InferredRedSkipped));
    }

    [Fact]
    public void SerialMode_RunsOneSuitePerTask()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), mode: BatchModes.Serial);
        Assert.Equal((4, 4), (s.FullSuiteRuns, s.TasksLanded));
        Assert.Equal(new[] { 1, 1, 1, 1 }, s.SizeTrace);
    }

    [Fact]
    public void EmptyTasks_Exit0WithoutWorktreeOrSuite()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, repo.WriteTasks());
        Assert.Equal((ExitCodes.Ok, 0, "empty batch"), (s.ExitCode, s.FullSuiteRuns, s.Note));
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, "int-E1")));
    }

    [Fact]
    public void GateTimeout_Exit5_SummaryWrittenWithUnprocessed()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("other suite");
        var s = BatchScenario.Run(repo, tasks, tweak: c => c with { MaxWaitSec = 1 });
        Assert.Equal(ExitCodes.GateTimeout, s.ExitCode);
        Assert.Contains("no test slot free", s.Note);
        Assert.Equal(4, s.Unprocessed.Count);
        Assert.True(File.Exists(Path.Combine(repo.StateDir, "runs", s.RunId, "summary.json")));
    }

    [Fact]
    public void ConcurrentRunOnSameEpic_FailsFast()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var layout = new StateLayout(repo.StateDir);
        using var held = new SlotSemaphore(layout.BatchLockDir("E1"), SlotOptions.From(TestConfig.For(repo))).Acquire("other batch run");
        var e = Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("another batch run holds epic 'E1'", e.Message);
        Assert.False(Directory.Exists(layout.RunsDir));
    }

    [Fact]
    public void SecondRun_ReusesIntegrationWorktreeWithStaleLock()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        Assert.Equal(ExitCodes.Ok, BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"))).ExitCode);
        var wt = Path.Combine(repo.WorktreeRoot, "int-E1");
        File.WriteAllText(Path.Combine(TempRepo.RunGit(wt, "rev-parse", "--absolute-git-dir"), "index.lock"), "");
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T2", "task/T2")));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.True(repo.HasFile("epic/E1", "two.txt"));
    }

    [Fact]
    public void LongWorktreeRoot_Exit2_NothingCreated()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var e = Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks, tweak: c => c with { WorktreeRoot = Path.Combine(repo.Sandbox, new string('w', 220)) }));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.False(Directory.Exists(repo.StateDir));
    }

    [Fact]
    public void EpicCheckedOut_Exit3()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        repo.Git("worktree", "add", "-q", Path.Combine(repo.Sandbox, "epicwt"), "epic/E1");
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks)).ExitCode);
    }

    [Fact]
    public void LanderTreeMismatch_Exit4()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), lander: new ShortLander());
        Assert.Equal(ExitCodes.Environment, s.ExitCode);
        Assert.Contains("differs from the tested tree", s.Note);
    }

    [Fact]
    public void LanderThrowsNonToolException_Exit4_SummaryWritten()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), lander: new ThrowingLander());
        Assert.Equal(ExitCodes.Environment, s.ExitCode);
        Assert.Contains("unexpected failure", s.Note);
        Assert.Contains("boom", s.Note);
        Assert.True(File.Exists(Path.Combine(repo.StateDir, "runs", s.RunId, "summary.json")));
        Assert.Contains("\"type\":\"run-end\"", File.ReadAllText(s.EventsFile));
    }

    [Fact]
    public void LanderNotAttemptedWithoutFailure_Exit4_NoRequeueLoop()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), lander: new NothingAttemptedLander());
        Assert.Equal(ExitCodes.Environment, s.ExitCode);
        Assert.Contains("not-attempted tasks without a failure", s.Note);
        Assert.Equal(1, s.FullSuiteRuns);
    }

    [Fact]
    public void InferredRed_NamesTheRedSuiteLog()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("t1.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("T2.fail", ""));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")));
        Assert.Equal((2, 1), (s.FullSuiteRuns, s.InferredRedSkipped));
        var reason = BatchScenario.Returned(s)["T2"].Reason;
        Assert.Contains("suite-001.log", reason);
        Assert.DoesNotContain("suite-002.log", reason);
    }

    [Fact]
    public void RightHalfThatConflictsOnReintegration_IsTestedNotInferredRed()
    {
        using var repo = Repo();

        // T3 is stacked on T1 without declaring it. After a squash-like landing of [T1, T2], T1's commit is not an
        // ancestor of the epic, so T3 conflicts (add/add on a.txt) and T4 is left alone: its tree is not the one
        // seen red, so it must be tested rather than blamed by inference.
        repo.Branch("task/T1", "epic/E1", ("a.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("t2.txt", "2\n"));
        repo.Branch("task/T3", "task/T1", ("a.txt", "3\n"), ("T3.fail", ""));
        repo.Branch("task/T4", "epic/E1", ("t4.txt", "4\n"));
        var tasks = repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2"), new TaskLine("T3", "task/T3"), new TaskLine("T4", "task/T4"));
        var s = BatchScenario.Run(repo, tasks, lander: new SquashLikeLander(), prebatch: false);
        Assert.Equal(0, s.InferredRedSkipped);
        Assert.Contains("T4", s.Landed.Select(l => l.Id));
        Assert.False(BatchScenario.Returned(s).ContainsKey("T4"));
    }
}
