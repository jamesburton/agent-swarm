using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class ConfigLoaderTests
{
    static void WriteDefaultConfig(TempRepo repo, string json)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".swarm"));
        File.WriteAllText(Path.Combine(repo.Root, ".swarm", "batch.json"), json);
    }

    [Fact]
    public void NoFile_UsesDefaults()
    {
        using var repo = TempRepo.Create();
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), null, ConfigOverrides.None);
        Assert.Equal(2, c.Slots);
        Assert.Equal((4, 2, 8), (c.Batch.Start, c.Batch.Min, c.Batch.Max));
        Assert.Equal(3600, c.MaxWaitSec);
        Assert.Equal(new[] { "dotnet", "test" }, c.TestCommand);
        Assert.Equal("epic/E1", c.EpicBranch);
    }

    [Fact]
    public void MainWorktreeFile_IsUsedFromLinkedWorktree()
    {
        using var repo = TempRepo.Create();
        WriteDefaultConfig(repo, """{ "slots": 3, "epic": "E7" }""");
        var side = Path.Combine(repo.Sandbox, "side");
        repo.Git("worktree", "add", "-q", "-b", "side", side);
        var c = ConfigLoader.Load(RepoLocator.Locate(side), null, ConfigOverrides.None);
        Assert.Equal(3, c.Slots);
        Assert.Equal("epic/E7", c.EpicBranch);
    }

    [Fact]
    public void FlagsWinOverFile()
    {
        using var repo = TempRepo.Create();
        WriteDefaultConfig(repo, """{ "slots": 3, "batch": { "start": 4, "min": 2, "max": 8 } }""");
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), null, new ConfigOverrides { Slots = 1, Max = 6 });
        Assert.Equal(1, c.Slots);
        Assert.Equal(6, c.Batch.Max);
        Assert.Equal(4, c.Batch.Start);
    }

    [Fact]
    public void CrlfAndComments_Accepted()
    {
        var c = ConfigLoader.Parse("{\r\n  // two slots\r\n  \"slots\": 2,\r\n  \"testCommand\": [\"npm\", \"test\"],\r\n}\r\n", "x.json");
        Assert.Equal(new[] { "npm", "test" }, c.TestCommand);
    }

    [Fact]
    public void ExplicitMissingFile_IsUsage()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => ConfigLoader.Load(RepoLocator.Locate(repo.Root), Path.Combine(repo.Sandbox, "nope.json"), ConfigOverrides.None));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains("not found", e.Message);
    }

    [Theory]
    [InlineData("""{ "slots": 0 }""", "slots must be >= 1")]
    [InlineData("""{ "batch": { "start": 2, "min": 4, "max": 8 } }""", "batch sizes must satisfy 1 <= min <= start <= max <= 64")]
    [InlineData("""{ "expirySec": 10, "heartbeatSec": 5 }""", "expirySec must be >= 3 x heartbeatSec")]
    [InlineData("""{ "heartbeatSec": 0 }""", "heartbeatSec must be >= 1")]
    [InlineData("""{ "pollMs": 5 }""", "pollMs must be between 10 and 60000")]
    [InlineData("""{ "maxWaitSec": -1 }""", "maxWaitSec must be >= 0")]
    [InlineData("""{ "testCommand": [] }""", "testCommand must be a non-empty array")]
    [InlineData("""{ "testCommand": ["dotnet", " "] }""", "testCommand must be a non-empty array")]
    [InlineData("""{ "epic": "E 1" }""", "epic must be a safe name")]
    [InlineData("""{ "epicBranchTemplate": "epic/x" }""", "epicBranchTemplate must contain {epic}")]
    [InlineData("""{ "baseBranch": "-x" }""", "baseBranch must be a branch name")]
    [InlineData("""{ "maxRebaseAttempts": 9 }""", "maxRebaseAttempts must be between 0 and 3")]
    [InlineData("""{ "keepRuns": 0 }""", "keepRuns must be >= 1")]
    [InlineData("""{ "worktreeRoot": "relative/wt" }""", "worktreeRoot must be an absolute path")]
    [InlineData("""{ "schemaVersion": 2 }""", "schemaVersion must be 1")]
    [InlineData("""{ "slot": 2 }""", "'slot'")]
    [InlineData("""{ "testCommand": null }""", "invalid config")]
    [InlineData("""not json""", "invalid config")]
    public void InvalidConfig_IsOneLineUsageError(string json, string expected)
    {
        var e = Assert.Throws<ToolException>(() => ConfigLoader.Validated(ConfigLoader.Parse(json, "batch.json"), "batch.json"));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("batch.json: ", e.Message);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void TestConfig_RoundTripsThroughFile()
    {
        using var repo = TempRepo.Create();
        var path = TestConfig.Write(repo, TestConfig.For(repo));
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), path, ConfigOverrides.None);
        Assert.Equal(1, c.Slots);
        Assert.Equal(repo.StateDir, c.StateDir);
    }
}
