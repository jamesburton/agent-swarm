using System.Globalization;
using System.Text;
using Swarm.Batching;
using Swarm.Git;
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
    public void RequireTicket_TicketlessTask_LandsNothing()
    {
        // Only the whole chain was tested, so landing T1 alone would put an untested state on the epic.
        using var repo = Repo();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/9933-c", "epic/E1", ("c.txt", "c\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9931-a"), T("T2"), T("T3", "task/9933-c"));
        var outcome = Land(request, new SquashConfig { RequireTicket = true });
        var result = outcome.Result;
        Assert.Empty(result.Landed);
        Assert.Empty(outcome.Commits);
        Assert.Equal("T2", result.Failure!.TaskId);
        Assert.Empty(result.Failure.Files);
        Assert.Contains("no ticket", result.Failure.GitOutput);
        Assert.DoesNotContain('\n', result.Failure.GitOutput);
        Assert.Equal(new[] { "T1", "T3" }, result.NotAttempted);
        Assert.Equal(request.EpicTipBefore, result.EpicTipAfter);
        Assert.Equal(request.EpicTipBefore, repo.Sha("epic/E1"));
    }

    [Fact]
    public void StackMemberWithoutTicket_LandsNothing()
    {
        using var repo = Repo();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/9932-b", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/T3", "task/9932-b", ("c.txt", "c\n"));
        repo.Branch("task/9934-d", "epic/E1", ("d.txt", "d\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9931-a"), T("T2", "task/9932-b"), T("T3", "task/T3", "T2"), T("T4", "task/9934-d"));
        var result = Land(request, new SquashConfig { RequireTicket = true }).Result;
        Assert.Empty(result.Landed);
        Assert.Equal("T3", result.Failure!.TaskId);
        Assert.Equal(new[] { "T1", "T2", "T4" }, result.NotAttempted);
        Assert.Equal(request.EpicTipBefore, repo.Sha("epic/E1"));
    }

    [Fact]
    public void RequireTicket_TicketlessNoOpTask_LandsEmpty()
    {
        // T2 adds exactly what T1 already added: its merge changes nothing, so no commit (and no ticket) is needed.
        using var repo = Repo();
        repo.Branch("task/9931-a", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "epic/E1", ("a.txt", "a\n"));
        var request = Tested(repo, Worktree(repo), T("T1", "task/9931-a"), T("T2"));
        var outcome = Land(request, new SquashConfig { RequireTicket = true });
        Assert.Null(outcome.Result.Failure);
        Assert.Empty(outcome.Result.NotAttempted);
        Assert.Equal(new[] { "T1", "T2" }, outcome.Result.Landed.Select(l => l.TaskId));
        Assert.Equal(new[] { false, true }, outcome.Commits.Select(c => c.Empty));
        Assert.Equal(1, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(repo.Sha(request.TestedCommit + "^{tree}"), repo.Sha("epic/E1^{tree}"));
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
    public void NonContiguousStack_ThrowsContractErrorAndLeavesEpic()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "epic/E1", ("b.txt", "b\n"));
        repo.Branch("task/T3", "task/T1", ("c.txt", "c\n"));
        var wt = Worktree(repo);
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");

        // The chain is in request order T1, T2, T3, but T3 is stacked on T1: the stack is not contiguous.
        var tested = wt.Integrate(tip, TaskUnits.Build([T("T1"), T("T2"), T("T3")])).Head;
        var tasks = new[] { new LandTask("T1", "task/T1", []), new LandTask("T2", "task/T2", []), new LandTask("T3", "task/T3", ["T1"]) };
        var request = new LandRequest(git, wt.Git, "E1", "epic/E1", tip, tested, tasks, 3, "run-1");
        var e = Assert.Throws<ToolException>(() => Land(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("'T3'", e.Message);
        Assert.DoesNotContain('\n', e.Message);
        Assert.Equal(tip, repo.Sha("epic/E1"));
    }

    [Fact]
    public void ContiguousStackThenSingle_LandsTestedTree()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("a.txt", "a\n"));
        repo.Branch("task/T2", "task/T1", ("b.txt", "b\n"));
        repo.Branch("task/T3", "epic/E1", ("c.txt", "c\n"));
        var request = Tested(repo, Worktree(repo), T("T1"), T("T2", "task/T2", "T1"), T("T3"));
        var result = Land(request).Result;
        Assert.Null(result.Failure);
        Assert.Equal(new[] { "T1", "T2", "T3" }, result.Landed.Select(l => l.TaskId));
        Assert.Equal(3, Count(repo, $"{request.EpicTipBefore}..epic/E1"));
        Assert.Equal(repo.Sha(request.TestedCommit + "^{tree}"), repo.Sha("epic/E1^{tree}"));
    }

    [Fact]
    public void TreeGuard_RunsBeforeTheEpicMoves()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var request = Tested(repo, Worktree(repo), T("T1"));

        // A rebuilt tip whose tree is not the tested one must never reach the epic.
        var lander = new SquashLander(new SquashConfig(), "main")
        {
            RebuiltTipOverride = tip => repo.Git("commit-tree", repo.Sha("epic/E1^{tree}"), "-p", tip, "-m", "tampered"),
        };
        var e = Assert.Throws<ToolException>(() => lander.Execute(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("differs from the tested tree", e.Message);
        Assert.Equal(request.EpicTipBefore, repo.Sha("epic/E1"));
    }

    [Fact]
    public void ManyLandedSources_ChunkedExclusionStillCreditsOnlyNewCommits()
    {
        using var repo = Repo();
        var wt = Worktree(repo);
        repo.Git("branch", "task/T1", "epic/E1");
        CommitAs(repo, "task/T1", "Ada", "ada@example.invalid", "add a", ("a.txt", "a\n"));
        Land(Tested(repo, wt, T("T1")));

        // 1,500 well-formed but absent Source-Commit shas (~61,500 argv chars): far past one exclusion chunk and the
        // 32,767-character Windows command line; the real T1 source lands in the last chunk.
        var message = new StringBuilder("bulk import\n\n");
        for (var i = 1; i <= 1500; i++)
        {
            message.Append("Source-Commit: ").Append(i.ToString("x40", CultureInfo.InvariantCulture)).Append('\n');
        }

        var messageFile = Path.Combine(repo.Sandbox, "bulk-msg.txt");
        File.WriteAllText(messageFile, message.ToString());
        var bulk = repo.Git("commit-tree", repo.Sha("epic/E1^{tree}"), "-p", repo.Sha("epic/E1"), "-F", messageFile);
        repo.Git("update-ref", "refs/heads/epic/E1", bulk);
        Assert.Equal(1500, Trailers(repo, "epic/E1", "Source-Commit").Split(',').Length);

        repo.Git("branch", "task/T2", "task/T1");
        CommitAs(repo, "task/T2", "Bob", "bob@example.invalid", "add b", ("b.txt", "b\n"));
        var request = Tested(repo, wt, T("T2"));
        var result = Land(request).Result;
        Assert.Null(result.Failure);
        Assert.Equal("Bob", repo.Git("log", "-1", "--format=%an", "epic/E1"));
        Assert.DoesNotContain("Ada", repo.Git("log", "-1", "--format=%B", "epic/E1"));
        Assert.Equal("T2: add b", repo.Git("log", "-1", "--format=%s", "epic/E1"));
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
