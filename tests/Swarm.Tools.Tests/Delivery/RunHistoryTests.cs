using Swarm.Delivery;
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

    [Fact]
    public void LandedBranches_IncludesWorkerBranchOfRebasedAndLanded()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(
            dir.Dir, "r1", T0, "epic/42-auth",
            [new LandedRecord("T1", 1, "c1", "task/T1"), new LandedRecord("T2", 2, "c2", "rebased/E1/T2")],
            [RunStateFixture.Returned("T2", "task/T2", FinalState.RebasedAndLanded, ReturnKind.Conflict)]);
        var branches = RunHistory.Load(new StateLayout(dir.Dir)).LandedBranches();
        Assert.Contains("task/T1", branches);
        Assert.Contains("task/T2", branches);
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
