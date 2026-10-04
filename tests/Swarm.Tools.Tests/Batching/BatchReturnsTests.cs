using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class BatchReturnsTests
{
    static TempRepo SharedFileRepo(string t2Branch = "task/T2")
    {
        var repo = TempRepo.Create();
        repo.Commit("shared", ("shared.txt", "base\n"));
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch(t2Branch, "epic/E1", ("shared.txt", "two\n"));
        return repo;
    }

    static TempRepo ThreeTaskRepo()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        for (var i = 1; i <= 3; i++)
        {
            repo.Branch($"task/T{i}", "epic/E1", ($"t{i}.txt", $"{i}\n"));
        }

        return repo;
    }

    static string ThreeTasks(TempRepo repo) => repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2"), new TaskLine("T3", "task/T3"));

    [Fact]
    public void MissingBranch_ReturnsTaskAndDependents_RunsRest_Exit1()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T10", "epic/E1", ("ten.txt", "10\n"));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T9", "task/T9"), new TaskLine("T10", "task/T10", ["T9"])));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1" }, s.Landed.Select(l => l.Id));
        var returned = BatchScenario.Returned(s);
        Assert.Equal((ReturnKind.BadInput, FinalState.ReturnedBadInput), (returned["T9"].Kind, returned["T9"].Final));
        Assert.Equal((ReturnKind.Dependency, FinalState.BlockedByDependency), (returned["T10"].Kind, returned["T10"].Final));
        Assert.Equal(2, s.BadInput);
    }

    [Fact]
    public void MissingBranch_IsCaughtBeforeMerge_NoConflictOrRebaseForIt()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T9", "task/T9"), new TaskLine("T1", "task/T1")));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1" }, s.Landed.Select(l => l.Id));
        var t9 = BatchScenario.Returned(s)["T9"];
        Assert.Equal((ReturnStage.Preflight, RebaseState.NotApplicable), (t9.Stage, t9.Rebase));
        Assert.False(new GitRunner(repo.Root).RefExists(GitRunner.HeadsRef("rebased/E1/T9")));
        Assert.DoesNotContain("\"type\":\"conflict\"", File.ReadAllText(s.EventsFile));
    }

    [Fact]
    public void ConflictInBatch_WithoutPrebatch_ReturnsOffenderAndNeedsWorkerAfterFailedRebase()
    {
        using var repo = SharedFileRepo();
        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), prebatch: false);
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal((1, 1), (s.Batches, s.FullSuiteRuns));
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((ReturnKind.Conflict, ReturnStage.Merge), (t2.Kind, t2.Stage));
        Assert.Equal(new[] { "T1" }, t2.ConflictingWith);
        Assert.Equal(new[] { "shared.txt" }, t2.Files);
        Assert.Equal((RebaseState.Conflict, FinalState.NeedsWorker), (t2.Rebase, t2.Final));
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.False(new GitRunner(repo.Root).RefExists("refs/heads/rebased/E1/T2"));
        Assert.Equal(1, s.NeedsWorker);
    }

    [Fact]
    public void WorkerBranchNamedLikeTheCopy_IsNeverRebasedOrDeleted()
    {
        using var repo = SharedFileRepo(t2Branch: "rebased/E1/T2");
        var worker = repo.Sha("rebased/E1/T2");
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "rebased/E1/T2")), prebatch: false);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((RebaseState.Skipped, FinalState.NeedsWorker), (t2.Rebase, t2.Final));
        Assert.Null(t2.RebasedBranch);
        Assert.Equal(worker, repo.Sha("rebased/E1/T2"));
    }

    [Fact]
    public void Prebatch_SeparatesSameFileTasks_ConflictIsAgainstLandedPartner()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")));
        Assert.Equal((2, 1), (s.Batches, s.FullSuiteRuns));
        Assert.Equal(new[] { "T1" }, BatchScenario.Returned(s)["T2"].ConflictingWith);
    }

    [Fact]
    public void LandFailure_RebasedCopyIsRequeuedAndLands_Exit0()
    {
        using var repo = ThreeTaskRepo();
        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, ThreeTasks(repo), lander: new FailOnceLander("T2"));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T3" }, s.Landed.Select(l => l.Id));
        Assert.Equal("rebased/E1/T2", s.Landed.Single(l => l.Id == "T2").Branch);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((ReturnStage.Land, RebaseState.Clean, FinalState.RebasedAndLanded), (t2.Stage, t2.Rebase, t2.Final));
        Assert.Equal("rebased/E1/T2", t2.RebasedBranch);
        Assert.Equal(1, s.RebasedAndLanded);
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.Equal(2, s.FullSuiteRuns);
    }

    [Fact]
    public void RequeuedCopyFailsAgain_SecondRebaseStartsFromWorkerBranch_WorkerNeverMoved()
    {
        using var repo = ThreeTaskRepo();
        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, ThreeTasks(repo), lander: new FailOnceLander("T2", failures: 2), tweak: c => c with { MaxRebaseAttempts = 2 });
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T3" }, s.Landed.Select(l => l.Id).Order(StringComparer.Ordinal));
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal(("task/T2", "rebased/E1/T2", FinalState.RebasedAndLanded), (t2.Branch, t2.RebasedBranch, t2.Final));
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.Equal(2, File.ReadAllLines(s.EventsFile).Count(l => l.Contains("\"type\":\"rebase\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void RequeuedCopyFailsAgain_AttemptsUsedUp_NeedsWorkerAndCopyKept()
    {
        using var repo = ThreeTaskRepo();
        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, ThreeTasks(repo), lander: new FailOnceLander("T2", failures: 2));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((RebaseState.Skipped, FinalState.NeedsWorker, "rebased/E1/T2"), (t2.Rebase, t2.Final, t2.RebasedBranch));
        Assert.True(new GitRunner(repo.Root).RefExists(GitRunner.HeadsRef("rebased/E1/T2")));
        Assert.Equal(worker, repo.Sha("task/T2"));
    }

    [Fact]
    public void ZeroRebaseAttempts_GoesStraightToNeedsWorker()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), tweak: c => c with { MaxRebaseAttempts = 0 }, prebatch: false);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((RebaseState.Skipped, FinalState.NeedsWorker), (t2.Rebase, t2.Final));
    }

    [Fact]
    public void ReturnedFile_IsAppendOnlyJsonlWithSchemaVersion()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), prebatch: false);
        var lines = File.ReadAllLines(s.ReturnedFile);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{\"schemaVersion\":1,", l));
    }
}
