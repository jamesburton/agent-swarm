using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Gate;

public class GateRunnerTests
{
    static SlotSemaphore Slots(string dir) =>
        new(Path.Combine(dir, "slots"), new SlotOptions(1, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), null));

    [Fact]
    public void Run_ReturnsChildExitCodeAndLog()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "x.fail"), "");
        var run = new GateRunner(Slots(dir.Dir)).Run(new GateRequest(FakeSuite.Command(), dir.Dir, "t"));
        Assert.Equal(1, run.Result.ExitCode);
        Assert.Equal("t", run.Result.Label);
        Assert.Contains("fake-suite: red (x.fail)", run.Log);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Dir, "slots")));
    }

    [Fact]
    public void CommandCannotStart_ReleasesSlot()
    {
        using var dir = new TempDir();
        var slots = Slots(dir.Dir);
        var e = Assert.Throws<ToolException>(() => new GateRunner(slots).Run(new GateRequest(["no-such-cmd-xyz"], dir.Dir, "t")));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        using var lease = slots.TryAcquire("next");
        Assert.NotNull(lease);
    }

    [Fact]
    public void EmptyCommand_IsUsage()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new GateRunner(Slots(dir.Dir)).Run(new GateRequest([], dir.Dir, "t"))).ExitCode);
    }

    [Fact]
    public void CancelledAsTheChildFails_ThrowsCancelled_NotARedResult()
    {
        using var dir = new TempDir();
        var slots = Slots(dir.Dir);
        using var cts = new CancellationTokenSource();

        // Ctrl+C arrives as the child reports its failure and exits non-zero at once: the process runner has already
        // seen the exit (it only polls the token while the child runs) and returns normally with exit code 1.
        var runner = new GateRunner(slots, line =>
        {
            if (line.Contains("nosuchcmd", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => runner.Run(new GateRequest(["git", "nosuchcmd"], dir.Dir, "t"), cts.Token));
        using var lease = slots.TryAcquire("next");
        Assert.NotNull(lease);
    }
}
