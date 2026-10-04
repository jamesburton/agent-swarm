using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class SlotSemaphoreTests
{
    static SlotOptions Options(int slots, double expirySec = 3, double heartbeatSec = 1, double? maxWaitSec = null) =>
        new(slots, TimeSpan.FromSeconds(expirySec), TimeSpan.FromSeconds(heartbeatSec), TimeSpan.FromMilliseconds(20), maxWaitSec is { } m ? TimeSpan.FromSeconds(m) : null);

    [Fact]
    public void Acquire_UsesFreeSlotsThenNone()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        var a = sem.Acquire("a");
        using var b = sem.Acquire("b");
        Assert.Equal((0, 1), (a.Slot, b.Slot));
        Assert.Null(sem.TryAcquire("c"));
        a.Dispose();
        Assert.False(File.Exists(a.LockPath));
        using var c = sem.TryAcquire("c");
        Assert.Equal(0, c!.Slot);
    }

    [Fact]
    public async Task ConcurrentHolders_NeverExceedSlots()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        var inside = 0;
        var peak = 0;
        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            using var lease = sem.Acquire($"worker {i}");
            var now = Interlocked.Increment(ref inside);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            Thread.Sleep(150);
            Interlocked.Decrement(ref inside);
        }));
        await Task.WhenAll(workers);
        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public void StaleLock_IsReclaimed()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "slot-0.lock");
        File.WriteAllText(path, "left by a crashed holder");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-120));
        using var lease = new SlotSemaphore(dir.Dir, Options(1)).Acquire("me");
        Assert.True(lease.Reclaimed);
        Assert.Equal(0, lease.Slot);
    }

    [Fact]
    public void Heartbeat_KeepsLockFreshPastExpiry()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(1, expirySec: 3, heartbeatSec: 1));
        using var lease = sem.Acquire("holder");
        Thread.Sleep(3500);
        Assert.True((DateTime.UtcNow - File.GetLastWriteTimeUtc(lease.LockPath)).TotalSeconds < 1.6);
        Assert.Null(sem.TryAcquire("intruder"));
    }

    [Fact]
    public void MaxWait_ThrowsGateTimeout()
    {
        using var dir = new TempDir();
        using var held = new SlotSemaphore(dir.Dir, Options(1)).Acquire("holder");
        var e = Assert.Throws<ToolException>(() => new SlotSemaphore(dir.Dir, Options(1, maxWaitSec: 0.3)).Acquire("waiter"));
        Assert.Equal(ExitCodes.GateTimeout, e.ExitCode);
        Assert.Contains("no test slot free after", e.Message);
    }

    [Fact]
    public void LiveLock_CannotBeDeletedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        using var lease = new SlotSemaphore(dir.Dir, Options(1)).Acquire("holder");
        Assert.NotNull(Record.Exception(() => File.Delete(lease.LockPath)));
        Assert.True(File.Exists(lease.LockPath));
    }

    [Fact]
    public void Status_ReportsHolderInfo()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        using var lease = sem.Acquire("dotnet test");
        var holder = Assert.Single(sem.Status());
        Assert.Equal(SwarmJson.SchemaVersion, holder.Info!.SchemaVersion);
        Assert.Equal(Environment.ProcessId, holder.Info.Pid);
        Assert.Equal("dotnet test", holder.Info.Command);
        using (var raw = new FileStream(lease.LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(raw))
        {
            Assert.StartsWith("{\"schemaVersion\":1,", reader.ReadToEnd());
        }

        Assert.False(holder.Stale);
        Assert.True(holder.HolderAlive);
    }

    [Fact]
    public void Reclaim_RemovesStaleAndDeadButNotLive()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(3, expirySec: 30));
        using var live = sem.Acquire("live");
        var dead = new LockInfo(SwarmJson.SchemaVersion, int.MaxValue - 7, Environment.MachineName, "dead", DateTime.UtcNow, DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(dir.Dir, "slot-1.lock"), SwarmJson.Line(dead));
        var stale = Path.Combine(dir.Dir, "slot-2.lock");
        File.WriteAllText(stale, "garbage");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5));
        var report = sem.Reclaim();
        Assert.Equal(new[] { 1, 2 }, report.Reclaimed);
        Assert.Equal(new[] { 0 }, report.SkippedLive);
        Assert.True(File.Exists(live.LockPath));
    }
}
