using Swarm.Batching;
using Swarm.Git;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.Tasks;

namespace Swarm.Tools.Tests.Batching;

public class IntegrationWorktreeTests
{
    static (TempRepo Repo, GitRunner Git, IntegrationWorktree Wt) Setup()
    {
        var repo = TempRepo.Create();
        repo.Commit("shared", ("shared.txt", "base\n"));
        repo.Epic();
        var git = new GitRunner(repo.Root);
        var wt = new IntegrationWorktree(git, Path.Combine(repo.WorktreeRoot, "int-E1"));
        wt.Ensure("epic/E1");
        return (repo, git, wt);
    }

    static string Status(IntegrationWorktree wt) => wt.Git.Run("status", "--porcelain");

    [Fact]
    public void Integrate_MergesCleanTasksInOrder()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var r = wt.Integrate(git.RevParse("refs/heads/epic/E1"), TaskUnits.Build([T("T1"), T("T2")]));
        Assert.Equal(2, r.Merged.Count);
        Assert.Empty(r.Conflicts);
        Assert.True(repo.HasFile(r.Head, "one.txt") && repo.HasFile(r.Head, "two.txt"));
    }

    [Fact]
    public void Integrate_StopsOnConflictReturnsOffenderAndContinues()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        repo.Branch("task/T3", "epic/E1", ("three.txt", "3\n"));
        var r = wt.Integrate(git.RevParse("refs/heads/epic/E1"), TaskUnits.Build([T("T1"), T("T2"), T("T3")]));
        Assert.Equal(new[] { "T1", "T3" }, r.Merged.Select(u => u.Id));
        var c = Assert.Single(r.Conflicts);
        Assert.Equal("T2", c.Offender.Id);
        Assert.Equal(new[] { "shared.txt" }, c.Files);
        Assert.Equal(new[] { "T1" }, c.MergedBefore);
        Assert.Contains("CONFLICT", c.GitOutput);
        Assert.Empty(Status(wt));
    }

    [Fact]
    public void Integrate_RollsBackAWholeStackOnConflict()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T0", "epic/E1", ("shared.txt", "zero\n"));
        repo.Branch("task/T1", "epic/E1", ("s1.txt", "1\n"));
        repo.Branch("task/T2", "task/T1", ("shared.txt", "two\n"));
        var tip = git.RevParse("refs/heads/epic/E1");
        var r = wt.Integrate(tip, TaskUnits.Build([T("T0"), T("T1"), T("T2", "T1")]));
        Assert.Equal(new[] { "T0" }, r.Merged.Select(u => u.Id));
        Assert.Equal(new[] { "T1", "T2" }, Assert.Single(r.Conflicts).Unit.Ids);
        Assert.False(repo.HasFile(r.Head, "s1.txt"));
    }

    // The user's repo signs every commit with a broken gpg and rejects every commit message and merge commit.
    static void BreakUserCommits(TempRepo repo)
    {
        repo.Git("config", "commit.gpgSign", "true");
        repo.Git("config", "gpg.program", "swarm-no-such-gpg");
        var hooks = Path.Combine(repo.Sandbox, "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var name in new[] { "commit-msg", "pre-merge-commit" })
        {
            File.WriteAllText(Path.Combine(hooks, name), "#!/bin/sh\necho rejected by test hook >&2\nexit 1\n");
        }

        repo.Git("config", "core.hooksPath", hooks.Replace('\\', '/'));

        // Sanity: both the signing and the hooks really fail an ordinary commit.
        var user = new GitRunner(repo.Root);
        Assert.NotEqual(0, user.Try("commit", "-q", "--allow-empty", "-m", "user commit").ExitCode);
        Assert.Contains("rejected by test hook", user.Try("-c", "commit.gpgSign=false", "commit", "-q", "--allow-empty", "-m", "user commit").StdErr);
    }

    [Fact]
    public void Integrate_IgnoresUserSigningAndCommitHooks()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        BreakUserCommits(repo);
        var r = wt.Integrate(git.RevParse("refs/heads/epic/E1"), TaskUnits.Build([T("T1")]));
        Assert.Empty(r.Conflicts);
        Assert.True(repo.HasFile(r.Head, "one.txt"));
    }

    [Fact]
    public void RebaseCopy_IgnoresUserSigning()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic moves", ("epic.txt", "e\n"));
        repo.Git("checkout", "-q", "main");
        BreakUserCommits(repo);
        var tip = git.RevParse("refs/heads/epic/E1");
        var outcome = wt.RebaseCopy(T("T1"), tip, "rebased/E1/T1");
        Assert.True(outcome.Clean, outcome.Output);
        Assert.True(repo.HasFile("rebased/E1/T1", "epic.txt") && repo.HasFile("rebased/E1/T1", "one.txt"));
    }

    [Fact]
    public void Ensure_CleansIndexLockAndLeftoverMerge()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        wt.Git.Run("merge", "--no-edit", "refs/heads/task/T1");
        Assert.NotEqual(0, wt.Git.Try("merge", "--no-edit", "refs/heads/task/T2").ExitCode);
        File.WriteAllText(Path.Combine(wt.Git.Run("rev-parse", "--absolute-git-dir"), "index.lock"), "");
        new IntegrationWorktree(git, wt.WorktreePath).Ensure("epic/E1");
        Assert.Empty(Status(wt));
        Assert.Equal(git.RevParse("refs/heads/epic/E1"), wt.Git.RevParse("HEAD"));
    }

    [Fact]
    public void Ensure_ForeignDirectory_IsEnvironmentError()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        var path = Path.Combine(repo.WorktreeRoot, "int-E1");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mine.txt"), "x");
        var e = Assert.Throws<ToolException>(() => new IntegrationWorktree(new GitRunner(repo.Root), path).Ensure("epic/E1"));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
    }

    [Fact]
    public void Constructor_TooLongPath_IsUsage()
    {
        using var repo = TempRepo.Create();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new IntegrationWorktree(new GitRunner(repo.Root), Path.Combine(repo.Sandbox, new string('w', 220)))).ExitCode);
    }

    [Fact]
    public void RebaseCopy_Clean_LeavesWorkerBranchIntact()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var worker = repo.Sha("task/T2");
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic moves", ("other.txt", "o\n"));
        repo.Git("checkout", "-q", "main");
        var tip = git.RevParse("refs/heads/epic/E1");
        var outcome = wt.RebaseCopy(T("T2"), tip, "rebased/E1/T2");
        Assert.True(outcome.Clean);
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.Equal(0, git.Try("merge-base", "--is-ancestor", tip, "refs/heads/rebased/E1/T2").ExitCode);
    }

    [Fact]
    public void RebaseCopy_WithUpdateRefs_NeverMovesWorkerOrSiblingBranches()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Git("config", "rebase.updateRefs", "true");
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "task/T1", ("two.txt", "2\n"));
        var t1 = repo.Sha("task/T1");
        var t2 = repo.Sha("task/T2");
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic moves", ("other.txt", "o\n"));
        repo.Git("checkout", "-q", "main");
        var outcome = wt.RebaseCopy(T("T2", "T1"), git.RevParse("refs/heads/epic/E1"), "rebased/E1/T2");
        Assert.True(outcome.Clean);
        Assert.Equal(t2, repo.Sha("task/T2"));
        Assert.Equal(t1, repo.Sha("task/T1"));
    }

    [Fact]
    public void RebaseCopy_Conflict_DeletesCopy()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic edits shared", ("shared.txt", "epic\n"));
        repo.Git("checkout", "-q", "main");
        var outcome = wt.RebaseCopy(T("T2"), git.RevParse("refs/heads/epic/E1"), "rebased/E1/T2");
        Assert.False(outcome.Clean);
        Assert.False(git.RefExists("refs/heads/rebased/E1/T2"));
        Assert.Empty(Status(wt));
    }

    [Fact]
    public void RebaseCopy_OntoTheTasksOwnBranch_ThrowsAndLeavesItAlone()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var worker = repo.Sha("task/T2");
        Assert.Throws<ArgumentException>(() => wt.RebaseCopy(T("T2"), git.RevParse("refs/heads/epic/E1"), "task/T2"));
        Assert.Equal(worker, repo.Sha("task/T2"));
    }

    [Fact]
    public void EnsureEpic_MissingOrCheckedOut_IsBadInput()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root);
        Assert.Contains("not found", Assert.Throws<ToolException>(() => RepoChecks.EnsureEpic(git, "epic/E1")).Message);
        repo.Epic();
        Assert.Equal(repo.Sha("epic/E1"), RepoChecks.EnsureEpic(git, "epic/E1"));
        repo.Git("worktree", "add", "-q", Path.Combine(repo.Sandbox, "epicwt"), "epic/E1");
        var e = Assert.Throws<ToolException>(() => RepoChecks.EnsureEpic(git, "epic/E1"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("is checked out in", e.Message);
    }

    [Fact]
    public void FastForwardLander_MovesEpicToTestedCommitWithCas()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var tip = git.RevParse("refs/heads/epic/E1");
        var head = wt.Integrate(tip, TaskUnits.Build([T("T1")])).Head;
        var request = new LandRequest(git, wt.Git, "E1", "epic/E1", tip, head, [new LandTask("T1", "task/T1", [])], 1, "run1");
        var result = new FastForwardLander().Land(request);
        Assert.Equal(head, result.EpicTipAfter);
        Assert.Equal(head, git.RevParse("refs/heads/epic/E1"));
        Assert.Equal("T1", Assert.Single(result.Landed).TaskId);
        var e = Assert.Throws<ToolException>(() => new FastForwardLander().Land(request));
        Assert.Contains("moved during the run", e.Message);
    }

    [Fact]
    public void FastForwardLander_LockedEpicRef_ReportsGitsReason()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var tip = git.RevParse("refs/heads/epic/E1");
        var head = wt.Integrate(tip, TaskUnits.Build([T("T1")])).Head;
        var request = new LandRequest(git, wt.Git, "E1", "epic/E1", tip, head, [new LandTask("T1", "task/T1", [])], 1, "run1");
        repo.LockRef("epic/E1");
        var e = Assert.Throws<ToolException>(() => new FastForwardLander().Land(request));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.StartsWith("could not move epic branch 'epic/E1': ", e.Message);
        Assert.EndsWith("; nothing landed for batch 1", e.Message);
        Assert.Contains(".lock", e.Hint);
        Assert.Equal(tip, git.RevParse("refs/heads/epic/E1"));
    }
}
