using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.SquashFixture;

namespace Swarm.Tools.Tests.Squashing;

public class SquashLanderTests
{
    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        return repo;
    }

    static SquashOutcome Land(LandRequest request, SquashConfig? config = null, string? ticket = null) =>
        new SquashLander(config ?? new SquashConfig(), "main", ticket).Execute(request);

    [Fact]
    public void Name_IsSquash() => Assert.Equal("squash", new SquashLander(new SquashConfig()).Name);

    [Fact]
    public void TwoTasks_OneCommitEach_LinearHistoryWithTestedTree()
    {
        using var repo = Repo();
        repo.Git("branch", "task/9933-parser", "epic/E1");
        CommitAs(repo, "task/9933-parser", "Ada", "ada@example.invalid", "add parser", ("p.txt", "p\n"));
        CommitAs(repo, "task/9933-parser", "Ada", "ada@example.invalid", "fix parser", ("p.txt", "p2\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9933-parser"), T("T2"));
        var result = Land(request).Result;
        Assert.Null(result.Failure);
        Assert.Empty(result.NotAttempted);
        Assert.Equal(repo.Sha("epic/E1"), result.EpicTipAfter);
        Assert.Equal(2, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(0, Count(repo, $"{request.EpicTipBefore}..epic/E1", "--merges"));
        Assert.Equal(repo.Sha(request.TestedCommit + "^{tree}"), repo.Sha("epic/E1^{tree}"));
        Assert.Equal(new[] { ("T1", repo.Sha("epic/E1~1")), ("T2", repo.Sha("epic/E1")) }, result.Landed.Select(l => (l.TaskId, l.Commit)));
        Assert.Equal("9933: add parser", repo.Git("log", "-1", "--format=%s", "epic/E1~1"));
        Assert.Contains("- fix parser", repo.Git("log", "-1", "--format=%b", "epic/E1~1"));
    }

    [Fact]
    public void Trailers_StampTicketEpicBatchRunTaskAndSource()
    {
        using var repo = Repo();
        var source = repo.Branch("task/9933-parser", "epic/E1", ("p.txt", "p\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        Land(Tested(repo, Worktree(repo), T("T1", "task/9933-parser"), T("T2")));
        Assert.Equal("9933", Trailers(repo, "epic/E1~1", "Ticket"));
        Assert.Equal("T2", Trailers(repo, "epic/E1", "Ticket"));
        Assert.Equal("E1", Trailers(repo, "epic/E1~1", "Epic"));
        Assert.Equal("3", Trailers(repo, "epic/E1~1", "Batch"));
        Assert.Equal("run-1", Trailers(repo, "epic/E1~1", "Swarm-Run"));
        Assert.Equal("T1", Trailers(repo, "epic/E1~1", "Task"));
        Assert.Equal(source, Trailers(repo, "epic/E1~1", "Source-Commit"));
    }

    [Fact]
    public void Author_IsOldestCommitAuthor_CommitterIsTool_OthersCoAuthor()
    {
        using var repo = Repo();
        repo.Git("branch", "task/T1", "epic/E1");
        CommitAs(repo, "task/T1", "Ada", "ada@example.invalid", "add a", ("a.txt", "a\n"));
        CommitAs(repo, "task/T1", "Bob", "bob@example.invalid", "add b", ("b.txt", "b\n"));
        Land(Tested(repo, Worktree(repo), T("T1")));
        Assert.Equal("Ada <ada@example.invalid>", repo.Git("log", "-1", "--format=%an <%ae>", "epic/E1"));
        Assert.Equal("swarm-batch", repo.Git("log", "-1", "--format=%cn", "epic/E1"));
        Assert.Equal("Bob <bob@example.invalid>", Trailers(repo, "epic/E1", "Co-authored-by"));
    }

    [Fact]
    public void ToolAuthorMode_CreditsEveryAuthorAsCoAuthor()
    {
        using var repo = Repo();
        repo.Git("branch", "task/T1", "epic/E1");
        CommitAs(repo, "task/T1", "Ada", "ada@example.invalid", "add a", ("a.txt", "a\n"));
        CommitAs(repo, "task/T1", "Bob", "bob@example.invalid", "add b", ("b.txt", "b\n"));
        Land(Tested(repo, Worktree(repo), T("T1")), new SquashConfig { Author = SquashAuthorModes.Tool });
        Assert.Equal("swarm-batch", repo.Git("log", "-1", "--format=%an", "epic/E1"));
        Assert.Equal("Ada <ada@example.invalid>,Bob <bob@example.invalid>", Trailers(repo, "epic/E1", "Co-authored-by"));
    }

    [Fact]
    public void Stack_SameTicket_OneCommit()
    {
        using var repo = Repo();
        repo.Branch("task/9933-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/9933-b", "task/9933-a", ("b.txt", "b\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9933-a"), T("T2", "task/9933-b", "T1"));
        var outcome = Land(request);
        Assert.Equal(1, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal("T1,T2", Trailers(repo, "epic/E1", "Task"));
        Assert.Equal(new[] { "T1", "T2" }, Assert.Single(outcome.Commits).TaskIds);
        Assert.All(outcome.Result.Landed, l => Assert.Equal(repo.Sha("epic/E1"), l.Commit));
    }

    [Fact]
    public void Stack_DifferentTickets_OneCommitPerTicketInOrder()
    {
        using var repo = Repo();
        repo.Branch("task/9933-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/9934-b", "task/9933-a", ("b.txt", "b\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9933-a"), T("T2", "task/9934-b", "T1"));
        Land(request);
        Assert.Equal(2, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(("9933", "9934"), (Trailers(repo, "epic/E1~1", "Ticket"), Trailers(repo, "epic/E1", "Ticket")));
    }

    [Fact]
    public void AlreadyContainedTask_NoCommitAndLandedAtTip()
    {
        using var repo = Repo();
        repo.Git("branch", "task/T1", "epic/E1");
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2"));
        var outcome = Land(request);
        Assert.Equal(1, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(request.EpicTipBefore, outcome.Result.Landed[0].Commit);
        Assert.True(outcome.Commits[0].Empty);
        Assert.Null(outcome.Commits[0].Commit);
    }

    [Fact]
    public void NetZeroDiff_NoCommitAndEpicUnchanged()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("tmp.txt", "x\n"));
        repo.Git("checkout", "-q", "task/T1");
        repo.Git("rm", "-q", "tmp.txt");
        repo.Git("commit", "-q", "-m", "remove tmp");
        repo.Git("checkout", "-q", "main");
        var request = Tested(repo, Worktree(repo), T("T1"));
        var outcome = Land(request);
        Assert.Equal(request.EpicTipBefore, repo.Sha("epic/E1"));
        Assert.Equal(request.EpicTipBefore, outcome.Result.EpicTipAfter);
        Assert.True(Assert.Single(outcome.Commits).Empty);
    }

    [Fact]
    public void StackedOnLandedBranch_CreditsOnlyNewCommits()
    {
        using var repo = Repo();
        var wt = Worktree(repo);
        repo.Git("branch", "task/T1", "epic/E1");
        CommitAs(repo, "task/T1", "Ada", "ada@example.invalid", "add a", ("a.txt", "a\n"));
        Land(Tested(repo, wt, T("T1")));
        repo.Git("branch", "task/T2", "task/T1");
        CommitAs(repo, "task/T2", "Bob", "bob@example.invalid", "add b", ("b.txt", "b\n"));
        Land(Tested(repo, wt, T("T2")));
        var git = new GitRunner(repo.Root);
        Assert.Equal("Bob", git.Run("log", "-1", "--format=%an", "epic/E1"));
        Assert.DoesNotContain("Ada", git.Run("log", "-1", "--format=%B", "epic/E1"));
        Assert.Equal("T2: add b", git.Run("log", "-1", "--format=%s", "epic/E1"));
    }

    [Fact]
    public void RebasedCopy_KeepsWorkerBranchTicket()
    {
        using var repo = Repo();
        var wt = Worktree(repo);
        repo.Branch("task/9932-b", "epic/E1", ("b.txt", "b\n"));
        Assert.True(wt.RebaseCopy(T("T2", "task/9932-b"), repo.Sha("epic/E1"), "rebased/E1/T2").Clean);
        Land(Tested(repo, wt, T("T2", "rebased/E1/T2")));
        Assert.Equal("9932", Trailers(repo, "epic/E1", "Ticket"));
    }

    [Fact]
    public void EpicMoved_NothingLanded()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        var moved = repo.Git("commit-tree", repo.Sha("epic/E1^{tree}"), "-p", request.EpicTipBefore, "-m", "human push");
        repo.Git("update-ref", "refs/heads/epic/E1", moved);
        var e = Assert.Throws<ToolException>(() => Land(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("moved during the run", e.Message);
        Assert.Equal(moved, repo.Sha("epic/E1"));
    }

    [Fact]
    public void EpicRefLocked_ReportsGitsReasonNotAMove()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        repo.LockRef("epic/E1");
        var e = Assert.Throws<ToolException>(() => Land(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not move epic branch 'epic/E1': ", e.Message);
        Assert.Contains("cannot lock", e.Message);
        Assert.DoesNotContain("moved during the run", e.Message);
        Assert.DoesNotContain('\n', e.Message);
        Assert.Contains(".lock", e.Hint);
        Assert.Equal(request.EpicTipBefore, repo.Sha("epic/E1"));
    }

    [Fact]
    public void NonAsciiAuthorAndSubject_RoundTrip()
    {
        using var repo = Repo();
        repo.Git("branch", "task/T1", "epic/E1");
        CommitAs(repo, "task/T1", "Zoë Ünal", "zoe@example.invalid", "Ünïcödé 日本語 change", ("u.txt", "u\n"));
        Land(Tested(repo, Worktree(repo), T("T1")));
        var git = new GitRunner(repo.Root);
        Assert.Equal("Zoë Ünal", git.Run("log", "-1", "--format=%an", "epic/E1"));
        Assert.Equal("T1: Ünïcödé 日本語 change", git.Run("log", "-1", "--format=%s", "epic/E1"));
    }

    [Fact]
    public void TreeGuard_Mismatch_ThrowsWithDiffstat()
    {
        using var repo = TempRepo.Create();
        var a = repo.Sha("HEAD");
        var b = repo.Commit("more", ("x.txt", "x\n"));
        var git = new GitRunner(repo.Root);
        TreeGuard.Ensure(git, a, a, "test");
        var e = Assert.Throws<ToolException>(() => TreeGuard.Ensure(git, a, b, "test"));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("differs from the tested tree", e.Message);
        Assert.Contains("x.txt", e.Message);
    }
}
