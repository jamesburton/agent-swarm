using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Slot semaphore settings.</summary>
/// <param name="Slots">Number of slots.</param>
/// <param name="Expiry">Age after which an unrefreshed lock is stale.</param>
/// <param name="Heartbeat">How often a holder refreshes its lock.</param>
/// <param name="Poll">How often a waiter retries.</param>
/// <param name="MaxWait">Longest wait before a gate timeout, or null for no limit.</param>
public sealed record SlotOptions(int Slots, TimeSpan Expiry, TimeSpan Heartbeat, TimeSpan Poll, TimeSpan? MaxWait)
{
    /// <summary>Builds options from config.</summary>
    /// <param name="config">The config.</param>
    /// <returns>The options (<c>maxWaitSec: 0</c> means no limit).</returns>
    public static SlotOptions From(SwarmConfig config) => new(
        config.Slots,
        TimeSpan.FromSeconds(config.ExpirySec),
        TimeSpan.FromSeconds(config.HeartbeatSec),
        TimeSpan.FromMilliseconds(config.PollMs),
        config.MaxWaitSec == 0 ? null : TimeSpan.FromSeconds(config.MaxWaitSec));
}

/// <summary>Content of a slot lock file.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Pid">Holder process id.</param>
/// <param name="Host">Holder machine name.</param>
/// <param name="Command">What the holder runs.</param>
/// <param name="AcquiredUtc">When the slot was taken.</param>
/// <param name="ProcessStartUtc">Holder process start time (guards against pid reuse).</param>
public sealed record LockInfo(int SchemaVersion, int Pid, string Host, string Command, DateTime AcquiredUtc, DateTime ProcessStartUtc);

/// <summary>A lock file as seen by <c>status</c>.</summary>
/// <param name="Slot">Slot index.</param>
/// <param name="Info">Parsed content, or null when unreadable (e.g. mid-write).</param>
/// <param name="HeartbeatAgeSec">Seconds since the last heartbeat.</param>
/// <param name="Stale">True when older than the expiry.</param>
/// <param name="HolderAlive">True when the holder is a running process (holders on other hosts count as alive).</param>
public sealed record SlotHolder(int Slot, LockInfo? Info, double HeartbeatAgeSec, bool Stale, bool HolderAlive);

/// <summary>stdout of <c>testgate status</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="LockDir">The slot lock directory.</param>
/// <param name="Slots">Configured slot count.</param>
/// <param name="Holders">Current lock files.</param>
public sealed record GateStatus(int SchemaVersion, string LockDir, int Slots, IReadOnlyList<SlotHolder> Holders);

/// <summary>stdout of <c>testgate reclaim --force</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Reclaimed">Slots whose lock was deleted.</param>
/// <param name="SkippedLive">Slots left alone because the holder is alive.</param>
public sealed record ReclaimReport(int SchemaVersion, IReadOnlyList<int> Reclaimed, IReadOnlyList<int> SkippedLive);

/// <summary>A held slot. Dispose to release it.</summary>
public sealed class SlotLease : IDisposable
{
    readonly FileStream stream;
    readonly Timer heartbeat;
    int disposed;

    internal SlotLease(int slot, string lockPath, FileStream stream, bool reclaimed, TimeSpan waited, DateTime acquiredUtc, TimeSpan heartbeatEvery)
    {
        Slot = slot;
        LockPath = lockPath;
        this.stream = stream;
        Reclaimed = reclaimed;
        Waited = waited;
        AcquiredUtc = acquiredUtc;
        heartbeat = new Timer(_ => Beat(), null, heartbeatEvery, heartbeatEvery);
    }

    /// <summary>Gets the slot index.</summary>
    public int Slot { get; }

    /// <summary>Gets a value indicating whether the slot was taken over from a stale lock.</summary>
    public bool Reclaimed { get; }

    /// <summary>Gets the time spent waiting.</summary>
    public TimeSpan Waited { get; }

    /// <summary>Gets when the slot was acquired.</summary>
    public DateTime AcquiredUtc { get; }

    /// <summary>Gets the lock file.</summary>
    public string LockPath { get; }

    /// <summary>Stops the heartbeat and deletes the lock (idempotent).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        heartbeat.Dispose();
        stream.Dispose();
        try
        {
            SharedFile.Retry(() => File.Delete(LockPath), attempts: 20, delayMs: 25);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind (e.g. a scanner holds it): it goes stale and is reclaimed after the expiry.
        }
    }

    // Touch mtime through the held handle: no second open, so no sharing violation with our own handle.
    void Beat()
    {
        try
        {
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Released concurrently; nothing to refresh.
        }
    }
}

/// <summary>
/// Machine-wide counting semaphore made of lock files: slot k is <c>slot-k.lock</c>, created atomically with
/// <see cref="FileMode.CreateNew"/>, refreshed by a heartbeat, reclaimed when older than the expiry.
/// </summary>
public sealed class SlotSemaphore
{
    /// <summary>Initializes a new instance of the <see cref="SlotSemaphore"/> class.</summary>
    /// <param name="lockDir">Directory holding the lock files.</param>
    /// <param name="options">Settings.</param>
    /// <exception cref="ToolException">The lock directory path is too long (exit code 2).</exception>
    public SlotSemaphore(string lockDir, SlotOptions options)
    {
        LockDir = StatePaths.Guard(Path.GetFullPath(lockDir), "lock dir");
        Options = options;
    }

    /// <summary>Gets the lock directory.</summary>
    public string LockDir { get; }

    /// <summary>Gets the settings.</summary>
    public SlotOptions Options { get; }

    /// <summary>Decides whether a lock's holder is still running.</summary>
    /// <param name="info">Lock content.</param>
    /// <returns>False for unreadable locks and dead local pids (or reused pids); true for live local and all remote holders.</returns>
    public static bool IsHolderAlive(LockInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        if (!string.Equals(info.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var p = Process.GetProcessById(info.Pid);
            return Math.Abs((p.StartTime.ToUniversalTime() - info.ProcessStartUtc).TotalSeconds) < 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Waits for a free slot.</summary>
    /// <param name="command">What the holder will run (recorded in the lock).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The lease.</returns>
    /// <exception cref="ToolException">No slot within <see cref="SlotOptions.MaxWait"/> (exit code 5).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public SlotLease Acquire(string command, CancellationToken cancellationToken = default)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryOnce(command, waited) is { } lease)
            {
                return lease;
            }

            if (Options.MaxWait is { } max && waited.Elapsed >= max)
            {
                throw new ToolException(
                    ExitCodes.GateTimeout,
                    $"no test slot free after {max.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s ({Options.Slots} slots in {LockDir})",
                    "raise --max-wait, or run 'testgate status' to see holders");
            }

            cancellationToken.WaitHandle.WaitOne(Options.Poll);
        }
    }

    /// <summary>Tries every slot once.</summary>
    /// <param name="command">What the holder will run.</param>
    /// <returns>The lease, or null when every slot is held.</returns>
    public SlotLease? TryAcquire(string command) => TryOnce(command, Stopwatch.StartNew());

    /// <summary>Lists the current lock files.</summary>
    /// <returns>One entry per lock file, by slot.</returns>
    public IReadOnlyList<SlotHolder> Status()
    {
        if (!Directory.Exists(LockDir))
        {
            return [];
        }

        var holders = new List<SlotHolder>();
        foreach (var path in Directory.EnumerateFiles(LockDir, "slot-*.lock"))
        {
            var name = Path.GetFileNameWithoutExtension(path)["slot-".Length..];
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || !File.Exists(path))
            {
                continue;
            }

            var info = ReadInfo(path);
            var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            holders.Add(new SlotHolder(slot, info, Math.Round(age, 1), age > Options.Expiry.TotalSeconds, IsHolderAlive(info)));
        }

        return holders.OrderBy(h => h.Slot).ToList();
    }

    /// <summary>Deletes stale locks and locks of dead local holders; a live local holder's lock is never deleted.</summary>
    /// <returns>What was reclaimed and what was skipped.</returns>
    public ReclaimReport Reclaim()
    {
        var reclaimed = new List<int>();
        var live = new List<int>();
        foreach (var h in Status())
        {
            var deadLocal = h.Info is not null
                && string.Equals(h.Info.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                && !h.HolderAlive;
            if (!h.Stale && !deadLocal)
            {
                live.Add(h.Slot);
                continue;
            }

            try
            {
                File.Delete(PathOf(h.Slot));
                reclaimed.Add(h.Slot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still open by a running process (Windows refuses the delete): treat as live.
                live.Add(h.Slot);
            }
        }

        return new ReclaimReport(SwarmJson.SchemaVersion, reclaimed, live);
    }

    static FileStream? TryCreate(string path)
    {
        try
        {
            // CreateNew is the atomic acquire. No FileShare.Delete: nobody can delete a live holder's lock on Windows.
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Delete-pending file from a holder that is releasing.
            return null;
        }
    }

    static LockInfo? ReadInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<LockInfo>(reader.ReadToEnd(), SwarmJson.Compact);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    string PathOf(int slot) => Path.Combine(LockDir, $"slot-{slot.ToString(CultureInfo.InvariantCulture)}.lock");

    bool IsStale(string path) => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Options.Expiry;

    SlotLease? TryOnce(string command, Stopwatch waited)
    {
        Directory.CreateDirectory(LockDir);
        for (var k = 0; k < Options.Slots; k++)
        {
            var path = PathOf(k);
            var reclaimed = false;
            var stream = TryCreate(path);
            if (stream is null && IsStale(path))
            {
                // Stale heartbeat: the holder crashed. Re-check, delete, race on CreateNew again (the loser keeps waiting).
                try
                {
                    if (IsStale(path))
                    {
                        File.Delete(path);
                        stream = TryCreate(path);
                        reclaimed = stream is not null;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Another reclaimer won, or the holder is alive after all.
                }
            }

            if (stream is not null)
            {
                return Lease(k, path, stream, reclaimed, waited.Elapsed, command);
            }
        }

        return null;
    }

    SlotLease Lease(int slot, string path, FileStream stream, bool reclaimed, TimeSpan waited, string command)
    {
        using var me = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var info = new LockInfo(SwarmJson.SchemaVersion, Environment.ProcessId, Environment.MachineName, TextLines.OneLine(command), now, me.StartTime.ToUniversalTime());
        stream.Write(Encoding.UTF8.GetBytes(SwarmJson.Line(info)));
        stream.Flush();
        return new SlotLease(slot, path, stream, reclaimed, waited, now, Options.Heartbeat);
    }
}
