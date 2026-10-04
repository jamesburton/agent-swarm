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
}
