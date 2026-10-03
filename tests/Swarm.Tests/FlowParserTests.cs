using Swarm.Core;

public class FlowParserTests
{
    [Theory]
    [InlineData("worker*", StageType.Fanout, "worker")]
    [InlineData("reviewer", StageType.Role, "reviewer")]
    [InlineData("gate:batch-green", StageType.Gate, "batch-green")]
    [InlineData("tool:squash", StageType.Tool, "squash")]
    [InlineData("  worker* ", StageType.Fanout, "worker")]
    public void ParseEntry_MapsGrammar(string entry, StageType type, string target) =>
        Assert.Equal(new Stage(type, target), FlowParser.ParseEntry(entry));

    [Fact] public void Parse_KeepsOrder() =>
        Assert.Equal(new[] { StageType.Fanout, StageType.Gate }, FlowParser.Parse(["a*", "gate:g"]).Select(s => s.Type));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gate:")]
    [InlineData("tool:")]
    [InlineData("*")]
    [InlineData("a b")]
    [InlineData("gate:x*")]
    public void ParseEntry_Invalid_Throws(string entry)
    {
        var ex = Assert.Throws<SwarmException>(() => FlowParser.ParseEntry(entry));
        Assert.DoesNotContain('\n', ex.Message);
    }
}
