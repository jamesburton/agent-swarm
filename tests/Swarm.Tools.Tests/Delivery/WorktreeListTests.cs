using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class WorktreeListTests
{
    [Fact]
    public void Parse_ForwardSlashPathsLockReasonAndPrunable()
    {
        var lines = new[]
        {
            "worktree C:/Repos/app", "HEAD 1111111111111111111111111111111111111111", "branch refs/heads/main", "",
            "worktree C:/Repos/app-wt/t-9933", "HEAD 2222222222222222222222222222222222222222", "branch refs/heads/feature/9933-login", "locked agent is working", "",
            "worktree C:/Repos/app-wt/int-E1", "HEAD 3333333333333333333333333333333333333333", "detached", "",
            "worktree C:/Repos/app-wt/t-1", "HEAD 4444444444444444444444444444444444444444", "branch refs/heads/task/1-x", "locked", "prunable gitdir file points to non-existent location",
        };
        var list = WorktreeList.Parse(lines);
        Assert.Equal(4, list.Count);
        Assert.Equal(Path.GetFullPath("C:/Repos/app-wt/t-9933"), list[1].Path);
        Assert.Equal("feature/9933-login", list[1].Branch);
        Assert.True(list[1].Locked);
        Assert.Equal("agent is working", list[1].LockReason);
        Assert.True(list[2].Detached);
        Assert.Null(list[2].Branch);
        Assert.True(list[3].Locked && list[3].Prunable);
        Assert.Null(list[3].LockReason);
    }

    [Fact]
    public void Read_FindsLinkedWorktreeAndCheckedOutBranch()
    {
        using var repo = TempRepo.Create();
        var side = Path.Combine(repo.Sandbox, "side");
        repo.Git("worktree", "add", "-q", "-b", "side", side);
        var git = new GitRunner(repo.Root);
        Assert.Equal(2, WorktreeList.Read(git).Count);
        Assert.True(WorktreeList.SamePath(side, WorktreeList.CheckedOut(git, "side")!.Path));
        Assert.Null(WorktreeList.CheckedOut(git, "nope"));
    }

    [Fact]
    public void SamePath_IgnoresSeparatorsTrailingSlashAndCaseOnWindows()
    {
        Assert.True(WorktreeList.SamePath("C:/a/b/", Path.Combine("C:", "a", "b")));
        Assert.Equal(OperatingSystem.IsWindows(), WorktreeList.SamePath("C:/A/B", "C:/a/b"));
    }
}
