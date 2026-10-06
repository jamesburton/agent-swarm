using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Git;

public class GitRunnerEnvironmentTests
{
    static readonly Dictionary<string, string?> Ada = new() { ["GIT_AUTHOR_NAME"] = "Ada", ["GIT_AUTHOR_EMAIL"] = "ada@example.invalid" };

    [Fact]
    public void WithEnvironment_SetsVariablesAndKeepsBaseOnes()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root).WithEnvironment(Ada);
        Assert.StartsWith("Ada <ada@example.invalid> ", git.Run("var", "GIT_AUTHOR_IDENT"));
        Assert.Equal("true", git.Run("var", "GIT_EDITOR"));
    }

    [Fact]
    public void WithIdentity_KeepsEnvironment_CommitterIsTool()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root).WithEnvironment(Ada).WithIdentity();
        Assert.StartsWith("Ada <ada@example.invalid> ", git.Run("var", "GIT_AUTHOR_IDENT"));
        Assert.StartsWith("swarm-batch <swarm-batch@example.invalid> ", git.Run("var", "GIT_COMMITTER_IDENT"));
    }

    [Fact]
    public void NullValue_RemovesVariable()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root).WithEnvironment(Ada).WithEnvironment(new Dictionary<string, string?> { ["GIT_AUTHOR_NAME"] = null, ["GIT_AUTHOR_EMAIL"] = null });
        Assert.StartsWith("test <test@example.invalid> ", git.Run("var", "GIT_AUTHOR_IDENT"));
    }

    [Fact]
    public void Utf8Output_RoundTrips()
    {
        // Git writes UTF-8 whatever the console code page is; decoding with the code page garbles non-ASCII text.
        using var repo = TempRepo.Create();
        repo.Commit("Zoë: naïve 日本語", ("ü.txt", "x\n"));
        var git = new GitRunner(repo.Root);
        Assert.Equal("Zoë: naïve 日本語", git.Run("log", "-1", "--format=%s"));
        Assert.Equal("ü.txt", git.Run("show", "--name-only", "--format=", "HEAD"));
    }
}
