using Swarm.Core;

public class RoleFieldsTests
{
    [Fact] public void AllNull_ReturnsNull() => Assert.Null(RoleFields.Check("r", null, null, null, null));

    [Theory]
    [InlineData("low")] [InlineData("medium")] [InlineData("high")] [InlineData("xhigh")] [InlineData("max")]
    public void ValidEfforts_Pass(string effort) => Assert.Null(RoleFields.Check("r", effort, "worktree", null, "distilled"));

    [Fact] public void MaxTurns_IsParsed() => Assert.Equal(7, RoleFields.Check("r", null, null, "7", null));

    [Theory]
    [InlineData("turbo", null, null, null, "unknown effort 'turbo' in role 'r'")]
    [InlineData(null, "docker", null, null, "unsupported isolation 'docker' in role 'r'")]
    [InlineData(null, null, "0", null, "bad maxTurns '0' in role 'r'")]
    [InlineData(null, null, "-1", null, "bad maxTurns '-1' in role 'r'")]
    [InlineData(null, null, "abc", null, "bad maxTurns 'abc' in role 'r'")]
    [InlineData(null, null, null, "forked", "unsupported context 'forked' in role 'r' (allowed: distilled)")]
    [InlineData(null, null, null, "Distilled", "unsupported context 'Distilled' in role 'r' (allowed: distilled)")]
    [InlineData(null, null, null, "", "unsupported context '' in role 'r' (allowed: distilled)")]
    public void Invalid_ThrowsExactMessage(string? effort, string? isolation, string? turns, string? context, string message) =>
        Assert.Equal(message, Assert.Throws<SwarmException>(() => RoleFields.Check("r", effort, isolation, turns, context)).Message);
}
