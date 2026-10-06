using Swarm.RunState.Cli;

namespace Swarm.Tools.Tests.Cli;

public class CtrlCScopeTests
{
    [Fact]
    public void FirstSignal_CancelsAndKeepsRunning_SecondLetsTheProcessTerminate()
    {
        using var scope = new CtrlCScope();
        Assert.True(scope.Signal());
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.False(scope.Signal());
    }

    [Fact]
    public void SignalAfterDispose_DoesNotThrow()
    {
        var scope = new CtrlCScope();
        scope.Dispose();
        scope.Signal();
    }
}
