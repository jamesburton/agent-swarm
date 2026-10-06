using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.RunState;

public class BranchTemplateTests
{
    static readonly WorktreeSection CiNaming = new()
    {
        BranchTemplate = "{kind}/{id}-{slug}",
        DefaultKind = "feature",
        AllowedPrefixes = ["feature/", "bugfix/"],
    };

    [Fact]
    public void Defaults_RenderTaskAndEpicBranches()
    {
        Assert.Equal("task/9933-login-form", BranchTemplate.Render(new WorktreeSection(), "9933", "login-form", null));
        Assert.Equal("epic/42-auth", BranchTemplate.Render(new EpicSection(), "42", "auth", null));
    }

    [Fact]
    public void CiTemplate_UsesDefaultKindOrGivenKind()
    {
        Assert.Equal("feature/9933-config-driven", BranchTemplate.Render(CiNaming, "9933", "config-driven", null));
        Assert.Equal("bugfix/9920-temp-files", BranchTemplate.Render(CiNaming, "9920", "temp-files", "bugfix"));
    }

    [Theory]
    [InlineData("fix")]
    [InlineData("feat")]
    [InlineData("hotfix")]
    public void ShortKind_IsRejectedWithCiHint(string kind)
    {
        var e = Assert.Throws<ToolException>(() => BranchTemplate.Render(CiNaming, "9933", "x", kind));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains($"branch '{kind}/9933-x' does not start with an allowed prefix (feature/, bugfix/)", e.Message);
        Assert.Contains("silently break CI", e.ErrorLine);
    }

    [Fact]
    public void WrongCasePrefix_IsRejected() =>
        Assert.False(BranchTemplate.HasAllowedPrefix("Feature/9933-x", ["feature/", "bugfix/"]));

    [Fact]
    public void NoRestriction_AllowsAnyPrefix()
    {
        Assert.True(BranchTemplate.HasAllowedPrefix("anything/x", null));
        Assert.True(BranchTemplate.HasAllowedPrefix("anything/x", []));
    }

    [Fact]
    public void KindForTemplateWithoutKind_IsUsage() =>
        Assert.Contains("has no {kind}", Assert.Throws<ToolException>(() => BranchTemplate.Render(new WorktreeSection(), "1", "x", "feature")).Message);

    [Theory]
    [InlineData("login-form", true)]
    [InlineData("a1-b2", true)]
    [InlineData("Login", false)]
    [InlineData("a--b", false)]
    [InlineData("-a", false)]
    [InlineData("a_b", false)]
    [InlineData("", false)]
    public void Slug_Rules(string slug, bool ok) => Assert.Equal(ok, BranchTemplate.IsValidSlug(slug));

    [Fact]
    public void LongSlug_IsInvalid() => Assert.False(BranchTemplate.IsValidSlug(new string('a', 41)));

    [Fact]
    public void UnsafeId_IsUsage() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => BranchTemplate.Render(new WorktreeSection(), "a b", "x", null)).ExitCode);
}
