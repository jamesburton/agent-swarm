using System.Diagnostics;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Git;

public class ProcessRunnerTests
{
    static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static bool GoneWithin(int pid, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (IsRunning(pid))
        {
            if (sw.Elapsed > limit)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    static ProcessResult Fake(string cwd, ProcessRunOptions? options = null, CancellationToken ct = default, params string[] extra)
    {
        var cmd = FakeSuite.Command(extra);
        return ProcessRunner.Run(cmd[0], cmd.Skip(1).ToList(), cwd, options, ct);
    }

    [Fact]
    public void Run_CapturesOutputLinesAndExitCode()
    {
        using var dir = new TempDir();
        var lines = new List<string>();
        var r = Fake(dir.Dir, new ProcessRunOptions { OnLine = lines.Add });
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.Killed);
        Assert.Contains("fake-suite: green", r.StdOut);
        Assert.Contains("fake-suite: green", lines);
    }

    [Fact]
    public void Run_MissingCommand_IsOneLineEnvironmentError()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ToolException>(() => ProcessRunner.Run("definitely-not-a-command-xyz", [], dir.Dir));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.StartsWith("error: cannot start 'definitely-not-a-command-xyz'", e.ErrorLine);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void Timeout_KillsWholeTree()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "child.pid");
        var r = Fake(dir.Dir, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(3) }, default, "--child-sleep-ms", "60000", "--pid-file", pidFile);
        Assert.True(r.Killed);
        Assert.Equal(-1, r.ExitCode);
        var child = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(GoneWithin(child, TimeSpan.FromSeconds(5)), "grandchild survived the tree kill");
    }

    [Fact]
    public void Cancel_KillsTreeAndThrows()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "child.pid");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Throws<OperationCanceledException>(() => Fake(dir.Dir, null, cts.Token, "--child-sleep-ms", "60000", "--pid-file", pidFile));
        var child = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(GoneWithin(child, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OrphanHoldingPipes_DoesNotHang()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "orphan.pid");
        var sw = Stopwatch.StartNew();
        try
        {
            var r = Fake(dir.Dir, new ProcessRunOptions { OutputGrace = TimeSpan.FromSeconds(1) }, default, "--orphan-ms", "20000", "--pid-file", pidFile);
            Assert.Equal(0, r.ExitCode);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                Kill(int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [Fact]
    public void OrphanOutputAfterGrace_IsNotDeliveredAfterRunReturns()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "orphan.pid");
        var count = 0;
        try
        {
            Fake(dir.Dir, new ProcessRunOptions { OutputGrace = TimeSpan.FromSeconds(1), OnLine = _ => Interlocked.Increment(ref count) }, default, "--orphan-ms", "4000", "--pid-file", pidFile);
            var atReturn = Volatile.Read(ref count);

            // The orphan writes its final line when its sleep ends, well after Run returned.
            Thread.Sleep(TimeSpan.FromSeconds(6));
            Assert.Equal(atReturn, Volatile.Read(ref count));
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                Kill(int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [Fact]
    public void Cancel_StopsDeliveringLines()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "child.pid");
        var count = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Throws<OperationCanceledException>(() => Fake(dir.Dir, new ProcessRunOptions { OnLine = _ => Interlocked.Increment(ref count) }, cts.Token, "--child-sleep-ms", "60000", "--pid-file", pidFile));
        var atThrow = Volatile.Read(ref count);
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Assert.Equal(atThrow, Volatile.Read(ref count));
    }

    [Fact]
    public void Resolve_FindsCmdShimViaPathExt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var shim = Path.Combine(dir.Dir, "hello.cmd");
        File.WriteAllText(shim, "@echo hi\r\n");
        Assert.Equal(shim, ProcessRunner.Resolve("hello", null, dir.Dir, ".EXE;.CMD"), ignoreCase: true);
        Assert.Contains("hi", ProcessRunner.Run(ProcessRunner.Resolve("hello", null, dir.Dir, ".EXE;.CMD"), [], dir.Dir).StdOut);
    }

    [Fact]
    public void Resolve_RelativePathIsAgainstWorkingDirectory()
    {
        using var dir = new TempDir();
        Assert.Equal(Path.Combine(dir.Dir, "tools", "x.exe"), ProcessRunner.Resolve("tools/x.exe", dir.Dir));
    }
}
