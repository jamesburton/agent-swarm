using Swarm.Git;

namespace Swarm.Tools.Tests.Git;

public class ToolErrorsTests
{
    [Fact]
    public void ExitCodes_AreStable() =>
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[] { ExitCodes.Ok, ExitCodes.Returned, ExitCodes.Usage, ExitCodes.BadInput, ExitCodes.Environment, ExitCodes.GateTimeout });

    [Fact]
    public void ToolException_IsOneLineWithHint()
    {
        var e = new ToolException(ExitCodes.BadInput, "tasks file\r\nis   bad", "fix it");
        Assert.Equal("error: tasks file is bad (fix it)", e.ErrorLine);
        Assert.Equal("tasks file is bad (fix it)", e.Summary);
    }

    [Fact]
    public void Handle_MapsToolExceptionToItsCode()
    {
        var err = new StringWriter();
        var code = ToolErrors.Handle(() => throw new ToolException(ExitCodes.GateTimeout, "waited"), err);
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Equal("error: waited", err.ToString().TrimEnd());
    }

    [Fact]
    public void Handle_UnexpectedExceptionIsEnvironmentAndOneLine()
    {
        var err = new StringWriter();
        var code = ToolErrors.Handle(() => throw new InvalidOperationException("boom\nsecond line"), err);
        Assert.Equal(ExitCodes.Environment, code);
        Assert.Equal("error: unexpected failure: InvalidOperationException: boom second line", err.ToString().TrimEnd());
    }

    [Fact]
    public void Handle_CancelledIsEnvironment()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Environment, ToolErrors.Handle(() => throw new OperationCanceledException(), err));
        Assert.Equal("error: cancelled", err.ToString().TrimEnd());
    }

    [Fact]
    public void OneLine_TruncatesLongText() =>
        Assert.Equal(TextLines.MaxOneLineLength, TextLines.OneLine(new string('x', 1000)).Length);

    [Theory]
    [InlineData("E1", true)]
    [InlineData("T001", true)]
    [InlineData("feature.9933_x-y", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a..b", false)]
    [InlineData("a.", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("T1.lock", false)]
    public void SafeName_Rules(string name, bool ok) => Assert.Equal(ok, SafeName.IsValid(name));
}
