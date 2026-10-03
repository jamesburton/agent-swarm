using Swarm.Core;

public class RoleFieldsTests
{
    [Fact] public void AllNull_ReturnsNull() => Assert.Null(RoleFields.Check("r", null, null, null));

    [Theory]
    [InlineData("low")] [InlineData("medium")] [InlineData("high")] [InlineData("xhigh")] [InlineData("max")]
    public void ValidEfforts_Pass(string effort) => Assert.Null(RoleFields.Check("r", effort, "worktree", null));

    [Fact] public void MaxTurns_IsParsed() => Assert.Equal(7, RoleFields.Check("r", null, null, "7"));

    [Theory]
    [InlineData("turbo", null, null, "unknown effort 'turbo' in role 'r'")]
    [InlineData(null, "docker", null, "unsupported isolation 'docker' in role 'r'")]
    [InlineData(null, null, "0", "bad maxTurns '0' in role 'r'")]
    [InlineData(null, null, "-1", "bad maxTurns '-1' in role 'r'")]
    [InlineData(null, null, "abc", "bad maxTurns 'abc' in role 'r'")]
    public void Invalid_ThrowsExactMessage(string? effort, string? isolation, string? turns, string message) =>
        Assert.Equal(message, Assert.Throws<SwarmException>(() => RoleFields.Check("r", effort, isolation, turns)).Message);
}
