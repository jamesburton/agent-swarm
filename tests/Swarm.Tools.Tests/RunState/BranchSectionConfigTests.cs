using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.RunState;

public class BranchSectionConfigTests
{
    static SwarmConfig Load(string json) => ConfigLoader.Validated(ConfigLoader.Parse(json, "batch.json"), "batch.json");

    static ToolException Invalid(string json) => Assert.Throws<ToolException>(() => Load(json));

    [Fact]
    public void PlanAConfigWithoutSections_GetsDefaults()
    {
        var c = Load("""{ "slots": 2 }""");
        Assert.Equal("task/{id}-{slug}", c.Worktree.BranchTemplate);
        Assert.Equal("epic/{id}-{slug}", c.EpicTool.BranchTemplate);
    }

    [Fact]
    public void PartialSection_KeepsItsOwnDefaultTemplate()
    {
        var c = Load("""{ "worktree": { "allowedPrefixes": ["task/"] } }""");
        Assert.Equal("task/{id}-{slug}", c.Worktree.BranchTemplate);
        Assert.Equal(new[] { "task/" }, c.Worktree.AllowedPrefixes);
    }

    [Fact]
    public void CiConfig_IsValid()
    {
        var c = Load("""
            {
              "worktree": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"] },
              "epicTool": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"] },
            }
            """);
        Assert.Equal("feature", c.EpicTool.DefaultKind);
    }

    [Theory]
    [InlineData("""{ "worktree": { "branchTemplate": "feat/{id}-{slug}", "allowedPrefixes": ["feature/", "bugfix/"] } }""", "worktree.branchTemplate renders 'feat/1-x', which does not start with an allowed prefix")]
    [InlineData("""{ "epicTool": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "fix", "allowedPrefixes": ["feature/", "bugfix/"] } }""", "epicTool.branchTemplate renders 'fix/1-x'")]
    public void TemplateOutsideAllowedPrefixes_IsUsage(string json, string expected)
    {
        var e = Invalid(json);
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void PrefixWithoutSlash_IsUsage() =>
        Assert.Contains("worktree.allowedPrefixes entries must end with '/' (got 'feature')", Invalid("""{ "worktree": { "allowedPrefixes": ["feature"] } }""").Message);

    [Theory]
    [InlineData("""{ "worktree": { "branchTemplate": "task/{id}" } }""", "worktree.branchTemplate must contain {id} and {slug}")]
    [InlineData("""{ "worktree": { "branchTemplate": "task/{id}-{slug}-{ticket}" } }""", "unknown placeholder '{ticket}'")]
    [InlineData("""{ "epicTool": { "branchTemplate": "{kind}/{id}-{slug}" } }""", "epicTool.defaultKind is required")]
    [InlineData("""{ "epicTool": { "defaultKind": "Feature" } }""", "epicTool.defaultKind must be lowercase letters")]
    [InlineData("""{ "worktree": { "branchTemplte": "x" } }""", "'branchTemplte'")]
    [InlineData("""{ "worktree": null }""", "invalid config")]
    public void InvalidSection_IsOneLineUsage(string json, string expected)
    {
        var e = Invalid(json);
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }
}
