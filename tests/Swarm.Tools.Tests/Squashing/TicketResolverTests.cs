using Swarm.Git;
using Swarm.RunState;
using Swarm.Squashing;

namespace Swarm.Tools.Tests.Squashing;

public class TicketResolverTests
{
    [Theory]
    [InlineData("feature/9933-squash-tool", "T1", "9933")]
    [InlineData("bugfix/9920-multi-node", "T1", "9920")]
    [InlineData("task/9933", "T1", "9933")]
    [InlineData("task/T1", "9940", "9940")]
    [InlineData("task/T1", "T1", "T1")]
    [InlineData("epic9933/x", "T7", "T7")]
    public void Resolve_BranchThenIdThenTaskId(string branch, string id, string expected) =>
        Assert.Equal(expected, TicketResolver.Resolve(new SquashConfig(), id, branch, null));

    [Fact]
    public void Override_Wins() => Assert.Equal("4242", TicketResolver.Resolve(new SquashConfig(), "T1", "task/9933-x", "4242"));

    [Fact]
    public void RequireTicket_NoMatch_IsNull() => Assert.Null(TicketResolver.Resolve(new SquashConfig { RequireTicket = true }, "T1", "task/T1", null));

    [Fact]
    public void CustomPattern() =>
        Assert.Equal("ABC-12", TicketResolver.Resolve(new SquashConfig { TicketPattern = @"(?<ticket>[A-Z]+-\d+)" }, "T1", "task/ABC-12-fix", null));

    [Fact]
    public void CatastrophicPattern_TimesOutAsUsageError()
    {
        var config = new SquashConfig { TicketPattern = @"^(?<ticket>(a+)+)$" };
        var ex = Assert.Throws<ToolException>(() => TicketResolver.Find(config, "T1", new string('a', 40) + "!"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("timed out", ex.Message);
    }
}
