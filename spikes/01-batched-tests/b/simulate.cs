#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1B: serial baseline vs adaptive batched for failing 0/1/2, plus crash-lock and slot-gate demos.
// Usage (from this folder): dotnet run simulate.cs -- --root C:\Development\agent-swarm-wt\s1b [--serial-actual] [--slots 2]
// Expects sandboxes <root>\f0, f1, f2 (sandbox-gen.cs --failing 0/1/2, same seed/layout). Writes results.json here.
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

string? Opt(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string root = Path.GetFullPath(Opt("root") ?? throw new ArgumentException("--root required"));
bool serialActual = args.Contains("--serial-actual");
string slots = Opt("slots") ?? "2";
string here = Directory.GetCurrentDirectory();
string overlapMap = Path.Combine(here, "overlap-map.json"); // SYNTHESIZED touches (generator tasks touch distinct files)
var jopt = new JsonSerializerOptions { WriteIndented = true };

(int code, string stdout, string stderr) Exec(string dir, string exe, params string[] a)
{
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    string o = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, o, err.Result);
}
JsonElement Batch(string sandbox, string mode, string stateName, bool withOverlap)
{
    string state = Path.Combine(root, "state-" + stateName);
    if (Directory.Exists(state)) Directory.Delete(state, true);
    var a = new List<string> { "run", "batch.cs", "--", sandbox, Path.Combine(sandbox, "tasks.json"), "--state", state, "--mode", mode, "--slots", slots, "--worktree", sandbox + "-wt" };
    if (withOverlap) { a.Add("--overlap-map"); a.Add(overlapMap); }
    Console.Error.WriteLine($"== batch.cs {stateName}");
    var r = Exec(here, "dotnet", a.ToArray());
    Console.Error.WriteLine(r.stderr.Length > 3000 ? r.stderr[^3000..] : r.stderr);
    string last = r.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.TrimStart().StartsWith('{'));
    return JsonDocument.Parse(last).RootElement.Clone();
}

// 1. single warm full-suite cost (worktree of f0 is built/warmed here and then reused by every run in that sandbox).
double singleSec = Batch(Path.Combine(root, "f0"), "measure", "measure", false).GetProperty("singleSuiteMs").GetDouble() / 1000.0;
Console.Error.WriteLine($"single full suite = {singleSec:0.0}s");

var metrics = new Dictionary<string, object>();
var baseline = new Dictionary<string, object>();
var notes = new List<string>();
bool allCorrect = true;

foreach (int f in new[] { 0, 1, 2 })
{
    string sb = Path.Combine(root, "f" + f);
    string src = File.ReadAllText(Path.Combine(sb, f == 0 ? "tasks.json" : Path.Combine("Lib06.Tests", "InteractionTests.cs")));
    var expectedPairs = Regex.Matches(src, @"Interaction\d+_(T\d+)_(T\d+)_").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    JsonElement ser;
    double serialWall;
    if (serialActual) { ser = Batch(sb, "serial", $"serial-f{f}", false); serialWall = ser.GetProperty("wallSeconds").GetDouble(); }
    else { ser = default; serialWall = double.NaN; }
    int serialRuns = 12; // serial = one full suite per task (always, red or green)
    var bat = Batch(sb, "batched", $"batched-f{f}", true);

    var rejected = bat.GetProperty("rejected").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
    var landedN = bat.GetProperty("landed").GetArrayLength();
    bool correct = rejected.Count == expectedPairs.Count
        && expectedPairs.All(p => rejected.Count(r => r == p.Item1 || r == p.Item2) == 1)
        && landedN == 12 - expectedPairs.Count;
    allCorrect &= correct;
    int runs = bat.GetProperty("fullSuiteRuns").GetInt32();

    metrics[$"f{f}.batched.fullSuiteRuns"] = runs;
    metrics[$"f{f}.batched.bisectRuns"] = bat.GetProperty("bisectRuns").GetInt32();
    metrics[$"f{f}.batched.inferredRedSkipped"] = bat.GetProperty("inferredRedSkipped").GetInt32();
    metrics[$"f{f}.batched.modelledCostSec"] = Math.Round(runs * singleSec, 1);
    metrics[$"f{f}.batched.wallSeconds"] = bat.GetProperty("wallSeconds").GetDouble();
    metrics[$"f{f}.batched.slotUtilisation"] = bat.GetProperty("slotUtilisation").GetDouble();
    metrics[$"f{f}.culpritCorrect"] = correct;
    metrics[$"f{f}.rejected"] = string.Join(",", rejected);
    metrics[$"f{f}.expectedPairs"] = string.Join(";", expectedPairs.Select(p => p.Item1 + "+" + p.Item2));
    metrics[$"f{f}.sizeTrace"] = string.Join(",", bat.GetProperty("sizeTrace").EnumerateArray().Select(x => x.GetInt32()));
    baseline[$"f{f}.serial.fullSuiteRuns"] = serialRuns;
    baseline[$"f{f}.serial.modelledCostSec"] = Math.Round(serialRuns * singleSec, 1);
    baseline[$"f{f}.serial.wallSeconds"] = serialActual ? serialWall : "not run (modelled)";
    if (f == 2)
    {
        metrics["preBatchOverlapPairs"] = bat.GetProperty("overlapPairs").GetInt32();
        metrics["preBatchSeparatedOverlaps"] = bat.GetProperty("overlapPairsSeparated").GetInt32();
        metrics["preBatchOverlapsInSameBatch"] = bat.GetProperty("overlapPairsSameBatch").GetInt32();
        baseline["naiveFixed4OverlapsInSameBatch"] = bat.GetProperty("naiveFixed4SameBatch").GetInt32();
    }
}
metrics["singleFullSuiteSec"] = Math.Round(singleSec, 1);
metrics["culpritCorrectAll"] = allCorrect;

// 2. crashed-lock recovery: kill a holder (whole tree) and show a new run reclaims after expiry.
{
    string cs = Path.Combine(root, "state-crash");
    if (Directory.Exists(cs)) Directory.Delete(cs, true);
    var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = here, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var x in new[] { "run", "testgate.cs", "--", "run", "--slots", "1", "--state", cs, "--heartbeat-sec", "1", "--expiry-sec", "6", "--", "powershell", "-NoProfile", "-Command", "Start-Sleep 600" }) psi.ArgumentList.Add(x);
    var holder = Process.Start(psi)!;
    holder.BeginOutputReadLine(); holder.BeginErrorReadLine();
    string lockFile = Path.Combine(cs, "slots", "slot-0.lock");
    var wait = Stopwatch.StartNew();
    while (!File.Exists(lockFile) && wait.Elapsed.TotalSeconds < 180) Thread.Sleep(200);
    bool held = File.Exists(lockFile);
    Thread.Sleep(2500); // let the heartbeat run at least once
    Exec(here, "taskkill", "/F", "/T", "/PID", holder.Id.ToString());
    var killedAt = Stopwatch.StartNew();
    var r2 = Exec(here, "dotnet", "run", "testgate.cs", "--", "run", "--slots", "1", "--state", cs, "--heartbeat-sec", "1", "--expiry-sec", "6", "--", "cmd", "/c", "exit", "0");
    string j = r2.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.TrimStart().StartsWith('{'));
    var d = JsonDocument.Parse(j).RootElement;
    bool reclaimed = d.GetProperty("reclaimed").GetBoolean();
    metrics["crashedLockRecovered"] = held && reclaimed;
    metrics["crashedLockReclaimWaitMs"] = d.GetProperty("waitMs").GetDouble();
    notes.Add($"Crash demo: holder lock created={held}; holder tree killed; new run reclaimed={reclaimed} after waitMs={d.GetProperty("waitMs").GetDouble()} (expiry 6 s demo flag, default 60 s).");
}

// 3. slot-gate demo: 3 concurrent 4 s commands through a 2-slot gate; one must wait.
{
    string gs = Path.Combine(root, "state-gate");
    if (Directory.Exists(gs)) Directory.Delete(gs, true);
    var procs = Enumerable.Range(0, 3).Select(_ =>
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = here, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "run", "testgate.cs", "--", "run", "--slots", "2", "--state", gs, "--", "powershell", "-NoProfile", "-Command", "Start-Sleep 4" }) psi.ArgumentList.Add(x);
        var p = Process.Start(psi)!; p.BeginErrorReadLine();
        return (p, o: p.StandardOutput.ReadToEndAsync());
    }).ToList();
    var res = procs.Select(x => { x.p.WaitForExit(); return JsonDocument.Parse(x.o.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.StartsWith('{'))).RootElement; }).ToList();
    double w = res.Sum(e => e.GetProperty("waitMs").GetDouble()), r = res.Sum(e => e.GetProperty("runMs").GetDouble());
    metrics["slotGateDemo.waitMsPerRun"] = string.Join(",", res.Select(e => e.GetProperty("waitMs").GetDouble()));
    metrics["slotGateDemo.waitFraction"] = Math.Round(w / (w + r), 3);
    metrics["slotGateDemo.maxConcurrentSlotsUsed"] = res.Select(e => e.GetProperty("slot").GetInt32()).Distinct().Count();
}

string verdict = allCorrect && metrics.TryGetValue("crashedLockRecovered", out var cr) && (bool)cr ? "works" : "partial";
var results = new
{
    spike = "01", variant = "b",
    question = "Does adaptive batch size + file-overlap pre-batching + halving bisect isolate culprits with fewer full-suite runs than serial?",
    metrics, baseline, verdict, notes,
    howToRun = "dotnet run simulate.cs -- --root C:\\Development\\agent-swarm-wt\\s1b   (sandboxes f0,f1,f2 from sandbox-gen.cs --projects 6 --tests-per 10 --delay-ms 100 --seed 1 --failing N --tasks 12)"
};
File.WriteAllText(Path.Combine(here, "results.json"), JsonSerializer.Serialize(results, jopt));
Console.WriteLine(JsonSerializer.Serialize(results, jopt));
return 0;
