#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1A: counting-semaphore "test gate" built from lock files.
// Usage: dotnet run testgate.cs -- run --slots N --state <dir> [--expiry-s 60] [--poll-ms 500] -- <cmd...>
// Prints {"waitMs":..,"runMs":..,"slot":K,"exitCode":..} on stdout; the child's output goes to stderr.
using System.Diagnostics;
using System.Text.Json;

if (args.Length == 0 || args[0] != "run") { Console.Error.WriteLine("usage: run --slots N --state <dir> -- <cmd...>"); return 2; }
int slots = 1, expiryS = 60, pollMs = 500, heartbeatS = 5; string? state = null;
int sep = Array.IndexOf(args, "--", 1);
if (sep < 0 || sep == args.Length - 1) { Console.Error.WriteLine("missing -- <cmd...>"); return 2; }
for (int i = 1; i < sep; i++)
{
    switch (args[i])
    {
        case "--slots": slots = int.Parse(args[++i]); break;
        case "--state": state = args[++i]; break;
        case "--expiry-s": expiryS = int.Parse(args[++i]); break;
        case "--poll-ms": pollMs = int.Parse(args[++i]); break;
        case "--heartbeat-s": heartbeatS = int.Parse(args[++i]); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}
if (state is null) { Console.Error.WriteLine("--state required"); return 2; }
string slotsDir = Path.Combine(state, "slots");
Directory.CreateDirectory(slotsDir);
var cmd = args[(sep + 1)..];

var wait = Stopwatch.StartNew();
int slot = -1;
while (slot < 0)
{
    for (int k = 0; k < slots && slot < 0; k++)
        if (TryAcquire(Path.Combine(slotsDir, $"slot-{k}"), expiryS)) slot = k;
    if (slot < 0) Thread.Sleep(pollMs);
}
long waitMs = wait.ElapsedMilliseconds;
string slotDir = Path.Combine(slotsDir, $"slot-{slot}");
string hb = Path.Combine(slotDir, "heartbeat");

// Heartbeat: touched every heartbeatS seconds while the holder lives; a dead holder stops touching it.
using var timer = new Timer(_ => { try { File.WriteAllText(hb, DateTime.UtcNow.ToString("O")); } catch { } }, null, 0, heartbeatS * 1000);

int exit;
var run = Stopwatch.StartNew();
try
{
    var psi = new ProcessStartInfo(cmd[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    p.WaitForExit();
    exit = p.ExitCode;
}
finally
{
    timer.Dispose();
    try { File.Delete(hb); } catch { }
    try { File.Delete(Path.Combine(slotDir, "owner.lock")); } catch { }
}
Console.WriteLine(JsonSerializer.Serialize(new { waitMs, runMs = run.ElapsedMilliseconds, slot, exitCode = exit }));
return exit;

// Atomic acquire: owner.lock created with FileMode.CreateNew (fails if it exists). A lock whose heartbeat
// (fallback: lock mtime) is older than expiryS is stale: it is renamed away (rename is atomic, only one
// contender wins), re-checked, and the acquire retried.
static bool TryAcquire(string dir, int expiryS)
{
    Directory.CreateDirectory(dir);
    string lockPath = Path.Combine(dir, "owner.lock");
    if (TryCreate(lockPath)) return true;
    if (!IsStale(dir, lockPath, expiryS)) return false;
    string moved = lockPath + ".stale." + Guid.NewGuid().ToString("N");
    try { File.Move(lockPath, moved); } catch { return false; }
    Console.Error.WriteLine($"[testgate] reclaimed stale lock in {dir}");
    try { File.Delete(moved); } catch { }
    return TryCreate(lockPath);
}

static bool TryCreate(string path)
{
    try
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var w = new StreamWriter(fs);
        w.Write($"{{\"pid\":{Environment.ProcessId},\"host\":\"{Environment.MachineName}\"}}");
        return true;
    }
    catch (IOException) { return false; }
}

static bool IsStale(string dir, string lockPath, int expiryS)
{
    string hbPath = Path.Combine(dir, "heartbeat");
    DateTime last;
    try { last = File.Exists(hbPath) ? File.GetLastWriteTimeUtc(hbPath) : File.GetLastWriteTimeUtc(lockPath); }
    catch { return false; }
    return (DateTime.UtcNow - last).TotalSeconds > expiryS;
}
