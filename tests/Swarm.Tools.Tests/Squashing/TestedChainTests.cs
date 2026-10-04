using Swarm.Batching;
using Swarm.Git;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.SquashFixture;

namespace Swarm.Tools.Tests.Squashing;

public class TestedChainTests
{
    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        return repo;
    }

    [Fact]
    public void Read_MapsEachTaskToItsMerge()
    {
        using var repo = Repo();
        var t1 = repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var t2 = repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2"));
        var links = TestedChain.Read(request);
        Assert.Equal(new[] { "T1", "T2" }, links.Select(l => l.Task.Id));
        Assert.Equal(request.EpicTipBefore, links[0].Before);
        Assert.Equal(links[0].After, links[1].Before);
        Assert.Equal(request.TestedCommit, links[1].After);
        Assert.Equal((t1, t2), (links[0].Source, links[1].Source));
        Assert.Equal(repo.Sha(request.TestedCommit + "^{tree}"), links[1].Tree);
    }

    [Fact]
    public void Read_AlreadyContainedTask_IsNoOpLink()
    {
        using var repo = Repo();
        repo.Git("branch", "task/T1", "epic/E1");
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2"));
        var links = TestedChain.Read(request);
        Assert.Equal((request.EpicTipBefore, request.EpicTipBefore, null), (links[0].Before, links[0].After, links[0].Source));
        Assert.Equal(repo.Sha("epic/E1^{tree}"), links[0].Tree);
        Assert.Equal(request.EpicTipBefore, links[1].Before);
    }

    [Fact]
    public void Read_StackMembersAreChained()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var t2 = repo.Branch("task/T2", "task/T1", ("two.txt", "2\n"));
        var links = TestedChain.Read(Tested(repo, Worktree(repo), T("T1"), T("T2", null, "T1")));
        Assert.Equal(links[0].After, links[1].Before);
        Assert.Equal(t2, links[1].Source);
    }

    [Fact]
    public void Read_ForeignCommit_IsEnvironmentError()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");
        var foreign = repo.Git("commit-tree", repo.Sha("task/T1^{tree}"), "-p", tip, "-m", "hand-made");
        var request = new LandRequest(git, git, "E1", "epic/E1", tip, foreign, [new LandTask("T1", "task/T1", [])], 1, "run-1");
        var e = Assert.Throws<ToolException>(() => TestedChain.Read(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("is not the epic tip plus one merge per task", e.Message);
    }

    [Fact]
    public void Read_WrongBase_IsEnvironmentError()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        var moved = repo.Git("commit-tree", repo.Sha("epic/E1^{tree}"), "-p", request.EpicTipBefore, "-m", "human");
        Assert.Equal(ExitCodes.Environment, Assert.Throws<ToolException>(() => TestedChain.Read(request with { EpicTipBefore = moved })).ExitCode);
    }

    [Fact]
    public void MergeSubject_MatchesIntegrationWorktree()
    {
        // Pins the coupling to Plan A: if IntegrationWorktree changes its merge message, this fails first.
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        Assert.Equal(TestedChain.MergeSubject(request.Tasks[0]), repo.Git("log", "-1", "--format=%s", request.TestedCommit));
    }

    static LandTask L(TaskSpec t) => new(t.Id, t.Branch, t.DependsOn);

    static ToolException Rejected(LandRequest request)
    {
        var e = Assert.Throws<ToolException>(() => TestedChain.Read(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("is not the epic tip plus one merge per task", e.Message);
        return e;
    }

    [Fact]
    public void Read_PrefixOnlyChain_IsRejected()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        var e = Rejected(request with { Tasks = [L(T("T1")), L(T("T2"))] });
        Assert.Contains("task 'T2' has no merge in the tested chain and its branch is not contained in", e.Message);
    }

    [Fact]
    public void Read_DroppedUnit_IsRejected()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T2"));
        var e = Rejected(request with { Tasks = [L(T("T1")), L(T("T2"))] });
        Assert.Contains("task 'T1' has no merge in the tested chain", e.Message);
    }

    [Fact]
    public void Read_MissingBranchWithoutMerge_IsRejected()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));
        Rejected(request with { Tasks = [L(T("T1")), L(T("T9"))] });
    }

    [Fact]
    public void Read_ReorderedTasks_IsRejected()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2"));
        Rejected(request with { Tasks = [L(T("T2")), L(T("T1"))] });
    }

    [Fact]
    public void Read_ExtraMergeBeyondTasks_IsRejected()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2"));
        var e = Rejected(request with { Tasks = [L(T("T1"))] });
        Assert.Contains($"commit {request.TestedCommit} is not the merge of the next task", e.Message);
    }

    [Fact]
    public void Read_MergeWithWrongFirstParent_IsRejected()
    {
        using var repo = Repo();
        var main = repo.Sha("main");
        var tip = repo.Git("commit-tree", repo.Sha("main^{tree}"), "-p", main, "-m", "epic work");
        repo.Git("update-ref", "refs/heads/epic/E1", tip);
        var source = repo.Branch("task/T1", "main", ("one.txt", "1\n"));
        var task = L(T("T1"));
        var tested = repo.Git("commit-tree", repo.Sha(source + "^{tree}"), "-p", main, "-p", source, "-m", TestedChain.MergeSubject(task));
        var git = new GitRunner(repo.Root);
        var e = Rejected(new LandRequest(git, git, "E1", "epic/E1", tip, tested, [task], 3, "run-1"));
        Assert.Contains($"merge of task 'T1' ({tested}) is not a two-parent merge onto {tip}", e.Message);
    }

    [Fact]
    public void Read_OctopusMerge_IsRejected()
    {
        using var repo = Repo();
        var s1 = repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var s2 = repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");
        var task = L(T("T1"));
        var tested = repo.Git("commit-tree", repo.Sha(s1 + "^{tree}"), "-p", tip, "-p", s1, "-p", s2, "-m", TestedChain.MergeSubject(task));
        var e = Rejected(new LandRequest(git, git, "E1", "epic/E1", tip, tested, [task], 3, "run-1"));
        Assert.Contains("is not a two-parent merge onto", e.Message);
    }

    [Fact]
    public void Read_NonMergeCommitWithMergeSubject_IsRejected()
    {
        using var repo = Repo();
        var s1 = repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");
        var task = L(T("T1"));
        var tested = repo.Git("commit-tree", repo.Sha(s1 + "^{tree}"), "-p", tip, "-m", TestedChain.MergeSubject(task));
        var e = Rejected(new LandRequest(git, git, "E1", "epic/E1", tip, tested, [task], 3, "run-1"));
        Assert.Contains("is not a two-parent merge onto", e.Message);
    }

    [Fact]
    public void Read_SpecialAndNonAsciiNames_RoundTrip()
    {
        using var repo = Repo();
        var task = T("T éü (1) %h", "task/ünï-cödé-%s");
        var source = repo.Branch(task.Branch, "epic/E1", ("one.txt", "1\n"));
        var links = TestedChain.Read(TestedSingle(repo, task));
        Assert.Equal(source, links[0].Source);
        Assert.Equal(task.Id, links[0].Task.Id);
    }
}
