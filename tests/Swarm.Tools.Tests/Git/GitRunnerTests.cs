using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Git;

public class GitRunnerTests
{
    [Fact]
    public void Failure_IsOneLineEnvironmentError()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => new GitRunner(repo.Root).Run("checkout", "no-such-branch"));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("git checkout no-such-branch failed in", e.ErrorLine);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void ForcesAutocrlfFalseOverRepoConfig()
    {
        using var repo = TempRepo.Create();
        repo.Git("config", "core.autocrlf", "true");
        Assert.Equal("false", new GitRunner(repo.Root).Run("config", "--get", "core.autocrlf"));
    }

    [Fact]
    public void RefExistsAndRevParse()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root);
        Assert.True(git.RefExists("refs/heads/main"));
        Assert.False(git.RefExists("refs/heads/nope"));
        Assert.Equal(repo.Sha("main"), git.RevParse("refs/heads/main"));
    }

    [Fact]
    public void WithIdentity_CommitsAsTool()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root).WithIdentity();
        git.Run("commit", "-q", "--allow-empty", "-m", "tool commit");
        Assert.Equal("swarm-batch", git.Run("log", "-1", "--format=%an"));
    }
}
