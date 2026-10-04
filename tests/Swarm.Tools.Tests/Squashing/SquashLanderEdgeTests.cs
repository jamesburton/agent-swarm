using Swarm.Batching;
using Swarm.RunState;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.SquashFixture;

namespace Swarm.Tools.Tests.Squashing;

public class SquashLanderEdgeTests
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
    public void RequireTicket_StopsAtFirstTaskWithoutTicket()
    {
        using var repo = Repo();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/9933-c", "epic/E1", ("c.txt", "c\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9931-a"), T("T2"), T("T3", "task/9933-c"));
        var result = Land(request, new SquashConfig { RequireTicket = true }).Result;
        Assert.Equal(new[] { "T1" }, result.Landed.Select(l => l.TaskId));
        Assert.Equal("T2", result.Failure!.TaskId);
        Assert.Contains("no ticket", result.Failure.GitOutput);
        Assert.Equal(new[] { "T3" }, result.NotAttempted);
        Assert.Equal(1, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(repo.Sha(request.TestedCommit + "~2^{tree}"), repo.Sha("epic/E1^{tree}"));
    }

    [Fact]
    public void StackMemberWithoutTicket_ReturnsWholeUnit()
    {
        using var repo = Repo();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/9932-b", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/T3", "task/9932-b", ("c.txt", "c\n"));
        repo.Branch("task/9934-d", "epic/E1", ("d.txt", "d\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9931-a"), T("T2", "task/9932-b"), T("T3", "task/T3", "T2"), T("T4", "task/9934-d"));
        var result = Land(request, new SquashConfig { RequireTicket = true }).Result;
        Assert.Equal(new[] { "T1" }, result.Landed.Select(l => l.TaskId));
        Assert.Equal("T3", result.Failure!.TaskId);
        Assert.Equal(new[] { "T2", "T4" }, result.NotAttempted);
        Assert.Equal(1, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
    }

    [Fact]
    public void TicketOverride_Wins()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        Land(Tested(repo, Worktree(repo), T("T1")), new SquashConfig { RequireTicket = true }, "4242");
        Assert.Equal("4242", Trailers(repo, "epic/E1", "Ticket"));
        Assert.StartsWith("4242: ", repo.Git("log", "-1", "--format=%s", "epic/E1"));
    }

    [Fact]
    public void NeverTouchesTaskBranchesOrCreatesRefs()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var wt = Worktree(repo);
        var request = Tested(repo, wt, T("T1"), T("T2"));
        var before = repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads/task/");
        Land(request);
        Assert.Equal(before, repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads/task/"));
        Assert.Equal(new[] { "refs/heads/epic/E1", "refs/heads/main", "refs/heads/task/T1", "refs/heads/task/T2" }, repo.Git("for-each-ref", "--format=%(refname)", "refs/heads/").Split('\n'));
        Assert.False(File.Exists(Path.Combine(TempRepo.RunGit(wt.WorktreePath, "rev-parse", "--absolute-git-dir"), SquashLander.MessageFileName)));
    }

    [Fact]
    public void CaseOnlyRename_LandsExactTree()
    {
        using var repo = TempRepo.Create();
        repo.Commit("foo", ("Foo.cs", "class Foo {}\n"));
        repo.Epic();
        repo.Git("checkout", "-q", "-b", "task/T1", "epic/E1");
        repo.Git("mv", "-f", "Foo.cs", "foo.cs");
        repo.Git("commit", "-q", "-m", "lowercase foo");
        repo.Git("checkout", "-q", "main");
        var request = TestedSingle(repo, T("T1"));
        Land(request);
        var names = repo.Git("ls-tree", "--name-only", "epic/E1").Split('\n');
        Assert.Contains("foo.cs", names);
        Assert.DoesNotContain("Foo.cs", names);
        Assert.Equal(repo.Sha(request.TestedCommit + "^{tree}"), repo.Sha("epic/E1^{tree}"));
    }

    [Fact]
    public void CrlfBytesAndExecutableBit_Preserved()
    {
        using var repo = Repo();
        repo.Git("checkout", "-q", "-b", "task/T1", "epic/E1");
        File.WriteAllBytes(Path.Combine(repo.Root, "win.txt"), "a\r\nb\r\n"u8.ToArray());
        repo.Write("run.sh", "echo hi\n");
        repo.Git("add", "win.txt", "run.sh");
        repo.Git("update-index", "--chmod=+x", "run.sh");
        repo.Git("commit", "-q", "-m", "crlf and exec");
        repo.Git("checkout", "-q", "main");
        Land(TestedSingle(repo, T("T1")));
        Assert.Equal(repo.Sha("task/T1:win.txt"), repo.Sha("epic/E1:win.txt"));
        Assert.StartsWith("100755", repo.Git("ls-tree", "epic/E1", "run.sh"));
    }
}
