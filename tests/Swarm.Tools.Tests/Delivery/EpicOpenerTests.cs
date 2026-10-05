using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicOpenerTests
{
    static EpicOpener Opener(TempRepo repo, Func<SwarmConfig, SwarmConfig>? tweak = null)
    {
        var config = TestConfig.For(repo);
        return new EpicOpener(RepoLocator.Locate(repo.Root), tweak?.Invoke(config) ?? config);
    }

    static SwarmConfig CiNaming(SwarmConfig c) => c with
    {
        EpicTool = new EpicSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
    };

    [Fact]
    public void Open_CreatesBranchAndRecordWithoutCheckout()
    {
        using var repo = TempRepo.Create();
        var r = Opener(repo).Open("42", "auth", null, null);
        Assert.True(r.Created);
        Assert.Equal(("epic/42-auth", "main", repo.Sha("main")), (r.Branch, r.BaseBranch, r.BaseCommit));
        Assert.Equal(repo.Sha("main"), repo.Sha("epic/42-auth"));
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        var record = new EpicStore(new StateLayout(repo.StateDir)).Get("42");
        Assert.Equal((EpicStates.Open, "epic/42-auth"), (record.State, record.Branch));
    }

    [Fact]
    public void Open_IsIdempotent()
    {
        using var repo = TempRepo.Create();
        Opener(repo).Open("42", "auth", null, null);
        Assert.False(Opener(repo).Open("42", "auth", null, null).Created);
    }

    [Fact]
    public void SameIdOtherSlug_IsBadInput()
    {
        using var repo = TempRepo.Create();
        Opener(repo).Open("42", "auth", null, null);
        var e = Assert.Throws<ToolException>(() => Opener(repo).Open("42", "login", null, null));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("epic '42' already exists as 'epic/42-auth'", e.Message);
    }

    [Fact]
    public void ExistingBranchWithoutRecord_IsBadInput()
    {
        using var repo = TempRepo.Create();
        repo.Epic(name: "epic/42-auth");
        Assert.Contains("already exists", Assert.Throws<ToolException>(() => Opener(repo).Open("42", "auth", null, null)).Message);
    }

    [Fact]
    public void From_MustExist()
    {
        using var repo = TempRepo.Create();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => Opener(repo).Open("42", "auth", "dev", null)).ExitCode);
        repo.Git("branch", "dev");
        Assert.Equal("dev", Opener(repo).Open("42", "auth", "dev", null).BaseBranch);
    }

    [Fact]
    public void CiTemplate_OpensFeatureBranch()
    {
        using var repo = TempRepo.Create();
        Assert.Equal("feature/9933-login", Opener(repo, CiNaming).Open("9933", "login", null, null).Branch);
    }

    [Fact]
    public void FixKind_CreatesNothing()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => Opener(repo, CiNaming).Open("9933", "login", null, "fix"));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Empty(repo.Git("branch", "--list", "fix/*"));
        Assert.Null(new EpicStore(new StateLayout(repo.StateDir)).Find("9933"));
    }

    [Theory]
    [InlineData("epic/{epic}", "epic/42-auth", "42-auth")]
    [InlineData("feature/{epic}", "feature/9933-login", "9933-login")]
    [InlineData("epic/{epic}", "feature/9933-login", null)]
    [InlineData("epic/{epic}/int", "epic/42/int", "42")]
    [InlineData("epic/{epic}", "epic/a/b", null)]
    public void BatchEpicId_InvertsTheBatchTemplate(string template, string branch, string? expected)
    {
        using var repo = TempRepo.Create();
        Assert.Equal(expected, EpicNaming.BatchEpicId(TestConfig.For(repo) with { EpicBranchTemplate = template }, branch));
    }

    [Fact]
    public void UnaddressableByBatch_WarnsWithNullBatchEpic()
    {
        using var repo = TempRepo.Create();
        var r = Opener(repo, CiNaming).Open("9933", "login", null, null);
        Assert.Null(r.BatchEpic);
        Assert.Contains(r.Warnings, w => w.Contains("epicBranchTemplate", StringComparison.Ordinal));
    }
}
