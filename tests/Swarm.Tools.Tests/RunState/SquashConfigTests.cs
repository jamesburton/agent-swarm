using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class SquashConfigTests
{
    [Fact]
    public void Defaults()
    {
        var c = new SwarmConfig();
        Assert.Equal(LanderNames.Squash, c.Lander);
        Assert.Equal(@"(?:^|/)(?<ticket>\d+)(?:-|$)", c.Squash.TicketPattern);
        Assert.False(c.Squash.RequireTicket);
        Assert.Equal("{ticket}: {title}", c.Squash.SubjectTemplate);
        Assert.Equal(SquashAuthorModes.Original, c.Squash.Author);
        Assert.Empty(ConfigLoader.Check(c));
    }

    [Fact]
    public void OldFileWithoutNewKeys_StillValid()
    {
        var c = ConfigLoader.Validated(ConfigLoader.Parse("""{ "slots": 3 }""", "batch.json"), "batch.json");
        Assert.Equal(LanderNames.Squash, c.Lander);
    }

    [Fact]
    public void ParsesLanderAndSquashSection()
    {
        var c = ConfigLoader.Validated(
            ConfigLoader.Parse("""{ "lander": "fast-forward", "squash": { "ticketPattern": "(?<ticket>[A-Z]+-\\d+)", "requireTicket": true, "subjectTemplate": "[{ticket}] {title}", "author": "tool" } }""", "batch.json"),
            "batch.json");
        Assert.Equal(LanderNames.FastForward, c.Lander);
        Assert.Equal(@"(?<ticket>[A-Z]+-\d+)", c.Squash.TicketPattern);
        Assert.True(c.Squash.RequireTicket);
        Assert.Equal("[{ticket}] {title}", c.Squash.SubjectTemplate);
        Assert.Equal(SquashAuthorModes.Tool, c.Squash.Author);
    }

    [Theory]
    [InlineData("""{ "lander": "rebase" }""", "lander must be 'squash' or 'fast-forward' (got 'rebase')")]
    [InlineData("""{ "squash": { "ticketPattern": "(?<ticket>" } }""", "squash.ticketPattern must be a valid regular expression with a named group 'ticket'")]
    [InlineData("""{ "squash": { "ticketPattern": "\\d+" } }""", "squash.ticketPattern must be a valid regular expression with a named group 'ticket'")]
    [InlineData("""{ "squash": { "subjectTemplate": "" } }""", "squash.subjectTemplate must be one non-empty line")]
    [InlineData("""{ "squash": { "subjectTemplate": "{ticket}\n{title}" } }""", "squash.subjectTemplate must be one non-empty line")]
    [InlineData("""{ "squash": { "subjectTemplate": "{tikcet}: {title}" } }""", "squash.subjectTemplate has unknown placeholder '{tikcet}'")]
    [InlineData("""{ "squash": { "author": "me" } }""", "squash.author must be 'original' or 'tool' (got 'me')")]
    [InlineData("""{ "squash": { "tiketPattern": "x" } }""", "'tiketPattern'")]
    [InlineData("""{ "squash": null }""", "invalid config")]
    public void InvalidConfig_IsOneLineUsageError(string json, string expected)
    {
        var e = Assert.Throws<ToolException>(() => ConfigLoader.Validated(ConfigLoader.Parse(json, "batch.json"), "batch.json"));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void TestConfig_RoundTripsSquashSection()
    {
        using var repo = TempRepo.Create();
        var path = TestConfig.Write(repo, TestConfig.For(repo) with { Lander = LanderNames.FastForward, Squash = new SquashConfig { RequireTicket = true } });
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), path, ConfigOverrides.None);
        Assert.Equal(LanderNames.FastForward, c.Lander);
        Assert.True(c.Squash.RequireTicket);
    }
}
