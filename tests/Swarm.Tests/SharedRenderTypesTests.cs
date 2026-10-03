using Swarm.Core;
using Swarm.Render;

public class SharedRenderTypesTests
{
    [Fact] public void RenderedFiles_EnumeratesInInsertionOrder()
    {
        var f = new RenderedFiles();
        f.Add("b", "1"); f.Add("a", "2"); f.Add("c", "3");
        Assert.Equal(new[] { "b", "a", "c" }, f.Keys);
        Assert.Equal(new[] { "1", "2", "3" }, f.Values);
        Assert.Equal(new[] { "b", "a", "c" }, f.Select(p => p.Key));
        Assert.Equal(3, f.Count);
    }

    [Fact] public void RenderedFiles_LookupAndMissingKey()
    {
        var f = new RenderedFiles();
        f.Add("a", "x");
        Assert.Equal("x", f["a"]);
        Assert.True(f.ContainsKey("a"));
        Assert.False(f.ContainsKey("A"));
        Assert.True(f.TryGetValue("a", out var v)); Assert.Equal("x", v);
        Assert.False(f.TryGetValue("nope", out _));
        Assert.Throws<KeyNotFoundException>(() => f["nope"]);
    }

    [Fact] public void RenderedFiles_RejectsDuplicateKey()
    {
        var f = new RenderedFiles();
        f.Add("a", "x");
        Assert.Throws<ArgumentException>(() => f.Add("a", "y"));
    }

    [Fact] public void SafeStems_AcceptsSafeNames() => SafeStems.Validate(["worker", "Expert_2", "a-b", new string('a', 64)], "role");

    [Theory]
    [InlineData("")] [InlineData("a b")] [InlineData("a/b")] [InlineData("..")] [InlineData("a.b")] [InlineData("worker\n")]
    [InlineData("CON")] [InlineData("nul")] [InlineData("Lpt1")]
    public void SafeStems_RejectsUnsafeNames(string name)
    {
        var e = Assert.Throws<SwarmException>(() => SafeStems.Validate([name], "role"));
        Assert.Contains("role '", e.Message);
        Assert.Contains("not a safe file name", e.Message);
        Assert.DoesNotContain('\n', e.Message);
    }

    [Fact] public void SafeStems_RejectsOverlongName() =>
        Assert.Throws<SwarmException>(() => SafeStems.Validate([new string('a', 65)], "swarm"));

    [Fact] public void SafeStems_RejectsCaseInsensitiveCollision() =>
        Assert.Contains("collides case-insensitively", Assert.Throws<SwarmException>(() => SafeStems.Validate(["Worker", "worker"], "role")).Message);
}
