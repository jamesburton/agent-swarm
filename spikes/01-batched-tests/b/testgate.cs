#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1B: CPU/test slot gate (counting semaphore built from lock files).
// Usage: dotnet run testgate.cs -- run --slots N --state <dir> [--expiry-sec 60] [--heartbeat-sec 5] [--poll-ms 200] [--cwd <dir>] -- <cmd...>
// Slot k = file <state>/slots/slot-k.lock, created atomically with FileMode.CreateNew. The holder touches the file's
// mtime every heartbeat; a lock whose mtime is older than the expiry is treated as left by a crashed holder and reclaimed.
// Child output goes to stderr; stdout is one JSON line {"waitMs","runMs","slot","exitCode","reclaimed"}.
using System.Diagnostics;
using System.Text.Json;

if (args.Length == 0 || args[0] != "run") { Console.Error.WriteLine("usage: testgate.cs -- run --slots N --state <dir> [--expiry-sec S] [--heartbeat-sec S] -- <cmd...>"); return 2; }
int sep = Array.IndexOf(args, "--", 1);
if (sep < 0 || sep == args.Length - 1) { Console.Error.WriteLine("missing -- <cmd...>"); return 2; }
string[] cmd = args[(sep + 1)..];
string? Opt(string name) { int i = Array.IndexOf(args, "--" + name, 1, sep - 1); return i >= 0 && i + 1 < sep ? args[i + 1] : null; }

int slots = int.Parse(Opt("slots") ?? "2");
string? state = Opt("state");
if (state is null) { Console.Error.WriteLine("--state required"); return 2; }
double expirySec = double.Parse(Opt("expiry-sec") ?? "60");
double heartbeatSec = double.Parse(Opt("heartbeat-sec") ?? "5");
int pollMs = int.Parse(Opt("poll-ms") ?? "200");
string slotsDir = Path.Combine(Path.GetFullPath(state), "slots");
Directory.CreateDirectory(slotsDir);

var wait = Stopwatch.StartNew();
FileStream? held = null;
string heldPath = "";
int slot = -1;
bool reclaimed = false;

FileStream? TryCreate(string path)
{
    try
    {
        // CreateNew is the atomic "acquire": exactly one process can create the file.
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; } // delete-pending file
}

while (held is null)
{
    for (int k = 0; k < slots && held is null; k++)
    {
        string path = Path.Combine(slotsDir, $"slot-{k}.lock");
        held = TryCreate(path);
        if (held is null && File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > expirySec)
        {
            // Stale heartbeat: holder crashed. Re-check, delete, then race on CreateNew again (loser just keeps waiting).
            // Known small window: a third process could delete a lock that a faster reclaimer just re-created; the
            // re-check above makes that unlikely, and a live holder's open handle (no FileShare.Delete) blocks the delete.
            try
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > expirySec)
                {
                    File.Delete(path);
                    held = TryCreate(path);
                    if (held is not null) reclaimed = true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (held is not null) { slot = k; heldPath = path; }
    }
    if (held is null) Thread.Sleep(pollMs);
}
long waitMs = wait.ElapsedMilliseconds;

using (var w = new StreamWriter(held, leaveOpen: true))
{
    w.Write(JsonSerializer.Serialize(new { pid = Environment.ProcessId, acquiredUtc = DateTime.UtcNow, cmd = string.Join(' ', cmd) }));
    w.Flush();
}

void Release()
{
    try { held?.Dispose(); File.Delete(heldPath); } catch { /* best effort; expiry covers the rest */ }
}
Console.CancelKeyPress += (_, _) => Release();
AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();

using var heartbeat = new Timer(_ =>
{
    try { File.SetLastWriteTimeUtc(heldPath, DateTime.UtcNow); } catch { }
}, null, TimeSpan.FromSeconds(heartbeatSec), TimeSpan.FromSeconds(heartbeatSec));

var psi = new ProcessStartInfo(cmd[0])
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = Path.GetFullPath(Opt("cwd") ?? Directory.GetCurrentDirectory()),
};
foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
var run = Stopwatch.StartNew();
int exit;
try
{
    using var p = Process.Start(psi)!;
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    p.WaitForExit();
    exit = p.ExitCode;
}
catch (Exception ex) { Console.Error.WriteLine("failed to start: " + ex.Message); exit = 127; }
long runMs = run.ElapsedMilliseconds;
heartbeat.Dispose();
Release();

Console.WriteLine(JsonSerializer.Serialize(new { waitMs, runMs, slot, exitCode = exit, reclaimed }));
return exit;
