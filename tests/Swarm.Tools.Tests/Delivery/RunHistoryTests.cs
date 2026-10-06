using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class RunHistoryTests
{
    static readonly DateTime T0 = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Load_OrdersByStartAndFiltersByEpic()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "zz-late", T0.AddHours(1), "epic/42-auth", [new LandedRecord("T2", 1, "c2", "task/T2")], []);
        RunStateFixture.WriteRun(dir.Dir, "aa-early", T0, "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], []);
        RunStateFixture.WriteRun(dir.Dir, "other", T0, "epic/7-x", [], []);
        var history = RunHistory.Load(new StateLayout(dir.Dir));
        Assert.Equal(new[] { "aa-early", "zz-late" }, history.ForEpic("epic/42-auth").Select(r => r.RunId));
    }

    [Fact]
    public void Outcomes_LaterLandingClearsEarlierReturn()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], [RunStateFixture.Returned("T3", "task/T3", FinalState.ReturnedRed)]);
        var once = RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs);
        Assert.True(once["T3"].Blocking);
        Assert.Equal(TaskStates.Landed, once["T1"].State);

        RunStateFixture.WriteRun(dir.Dir, "r2", T0.AddHours(1), "epic/42-auth", [new LandedRecord("T3", 1, "c3", "task/T3")], []);
        Assert.False(RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs)["T3"].Blocking);
    }

    [Fact]
    public void Outcomes_UnprocessedIsBlocking_NoOpIsNot()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [], [RunStateFixture.Returned("T2", "task/T2", FinalState.NoOpAfterRebase, ReturnKind.Conflict)], ["T4"]);
        var o = RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs);
        Assert.Equal(TaskStates.Unprocessed, o["T4"].State);
        Assert.True(o["T4"].Blocking);
        Assert.False(o["T2"].Blocking);
    }

    [Fact]
    public void UnfinishedRun_TakesEpicFromRunStartEvent()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "crashed", T0, "epic/42-auth", [], [], finished: false);
        var run = Assert.Single(RunHistory.Load(new StateLayout(dir.Dir)).ForEpic("epic/42-auth"));
        Assert.False(run.Finished);
        Assert.Equal(T0, run.StartedUtc);
    }

    static HashSet<string> Landed(TempRepo repo, string stateDir, string epic = "epic/42-auth") =>
        [.. RunHistory.Load(new StateLayout(stateDir)).LandedBranches(new GitRunner(repo.Root), epic)];

    static TempRepo RepoWithTask(params string[] branches)
    {
        var repo = TempRepo.Create();
        repo.Epic(name: "epic/42-auth");
        foreach (var b in branches)
        {
            repo.Branch(b, "epic/42-auth", ("f-" + b.Replace('/', '_') + ".txt", "x\n"));
        }

        return repo;
    }

    [Fact]
    public void LandedBranches_IncludesWorkerBranchOfRebasedAndLanded()
    {
        using var repo = RepoWithTask("task/T1", "task/T2");
        var started = DateTime.UtcNow.AddHours(1);
        RunStateFixture.WriteRun(
            repo.StateDir, "r1", started, "epic/42-auth",
            [new LandedRecord("T1", 1, "c1", "task/T1"), new LandedRecord("T2", 2, "c2", "rebased/E1/T2")],
            [RunStateFixture.Returned("T2", "task/T2", FinalState.RebasedAndLanded, ReturnKind.Conflict)]);
        var branches = Landed(repo, repo.StateDir);
        Assert.Contains("task/T1", branches);
        Assert.Contains("task/T2", branches);
    }

    [Fact]
    public void LandedBranches_OmitsBranchCommittedAfterTheLandingRunStarted()
    {
        using var repo = RepoWithTask("task/T1");
        RunStateFixture.WriteRun(repo.StateDir, "r1", DateTime.UtcNow.AddHours(-1), "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], []);
        var set = Landed(repo, repo.StateDir);
        Assert.Empty(set);
        Assert.Null(MergeCheck.LandedVia(new GitRunner(repo.Root), "task/T1", "epic/42-auth", set));
    }

    [Fact]
    public void LandedBranches_UnchangedLandedBranchIsTrusted()
    {
        using var repo = RepoWithTask("task/T1");
        RunStateFixture.WriteRun(repo.StateDir, "r1", DateTime.UtcNow.AddHours(1), "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], []);
        var set = Landed(repo, repo.StateDir);
        Assert.Contains("task/T1", set);
        Assert.Equal(MergeVia.Ledger, MergeCheck.LandedVia(new GitRunner(repo.Root), "task/T1", "epic/42-auth", set));
    }

    [Fact]
    public void LandedBranches_AreScopedToTheEpic()
    {
        using var repo = RepoWithTask("task/T1");
        RunStateFixture.WriteRun(repo.StateDir, "r1", DateTime.UtcNow.AddHours(1), "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], []);
        Assert.Contains("task/T1", Landed(repo, repo.StateDir, "epic/42-auth"));
        Assert.Empty(Landed(repo, repo.StateDir, "epic/7-x"));
    }

    [Fact]
    public void LandedBranches_MissingBranchIsOmitted()
    {
        using var repo = RepoWithTask();
        RunStateFixture.WriteRun(repo.StateDir, "r1", DateTime.UtcNow.AddHours(1), "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/gone")], []);
        Assert.Empty(Landed(repo, repo.StateDir));
    }

    [Fact]
    public void MissingStateDir_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(RunHistory.Load(new StateLayout(Path.Combine(dir.Dir, "none"))).Runs);
    }

    [Fact]
    public void CorruptSummary_CountsAsUnfinished()
    {
        using var dir = new TempDir();
        var run = RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [], []);
        File.WriteAllText(Path.Combine(run, RunDirectories.SummaryFileName), "{ broken");
        Assert.False(Assert.Single(RunHistory.Load(new StateLayout(dir.Dir)).Runs).Finished);
    }
}
