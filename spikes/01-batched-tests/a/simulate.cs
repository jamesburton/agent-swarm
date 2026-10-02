#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1A: runs serial baseline (batch size 1) vs batched (size N) on sandboxes with failing = 0,1,2,
// plus a slot-contention demo and a crashed-lock recovery demo; writes results.json.
// Usage (from this folder): dotnet run simulate.cs -- --root C:\Development\agent-swarm-wt\s1a [--size 4] [--expiry-s 10] [--out results.json]
// Expects sandboxes <root>\f0, f1, f2 (see RESULTS.md) and runs from the folder containing batch.cs / testgate.cs.
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

string root = "", outPath = "results.json"; int size = 4, expiryS = 10; bool skipModes = false, reuse = false; string serialFor = "";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = args[++i]; break;
        case "--size": size = int.Parse(args[++i]); break;
        case "--expiry-s": expiryS = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--reuse": reuse = true; break; // reuse existing f<N>-<mode>.json outputs
        case "--serial-for": serialFor = args[++i]; break; // comma list of failing counts whose serial baseline is actually run; others are modelled as one run per task
        case "--skip-modes": skipModes = true; break; // only the lock demos
    }
}
string here = Directory.GetCurrentDirectory();
string gateCs = Path.Combine(here, "testgate.cs"), batchCs = Path.Combine(here, "batch.cs");
var jo = new JsonSerializerOptions { WriteIndented = true };
var modes = new Dictionary<string, object>();
var notes = new List<string>();
double fullSuiteMs = 0; var allRunMs = new List<long>(); bool allCorrect = true;

if (!skipModes)
{
    foreach (int f in new[] { 0, 1, 2 })
    {
        string sb = Path.Combine(root, $"f{f}"), tasksJson = Path.Combine(sb, "tasks.json");
        var pairs = Regex.Matches(File.ReadAllText(Path.Combine(sb, "README.md")), @"\| (T\d+) \+ (T\d+) \|")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        int nTasks = JsonDocument.Parse(File.ReadAllText(tasksJson)).RootElement.GetArrayLength();
        foreach (var (label, sz) in new[] { ("serial", 1), ("batched", size) })
        {
            string o = Path.Combine(root, $"f{f}-{label}.json");
            if (label == "serial" && !serialFor.Split(',').Contains(f.ToString()))
            {
                // Serial baseline is by definition one full suite per task; modelled, not executed, to save wall time under load.
                modes[$"failing{f}.serial"] = new { batchSize = 1, fullSuiteRuns = nTasks, measured = false };
                continue;
            }
            var sw = Stopwatch.StartNew();
            if (!(reuse && File.Exists(o))) {
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "run", batchCs, "--", sb, tasksJson, "--state", Path.Combine(root, $"st-f{f}-{label}"), "--worktree", Path.Combine(root, $"w{f}"),
                "--size", sz.ToString(), "--out", o }) psi.ArgumentList.Add(a);
            psi.Environment["TESTGATE_CS"] = gateCs;
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.StartsWith("[batch]")) Console.Error.WriteLine($"f{f}/{label} {e.Data}"); };
            p.BeginErrorReadLine(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
            sw.Stop();
            if (p.ExitCode != 0) { notes.Add($"f{f}/{label}: batch.cs exit {p.ExitCode}"); continue; }
            }
            var r = JsonDocument.Parse(File.ReadAllText(o)).RootElement;
            var runs = r.GetProperty("runs").EnumerateArray().ToList();
            var ret = r.GetProperty("returned").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToHashSet();
            var land = r.GetProperty("landed").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            // Correct isolation: exactly one member of each seeded pair returned (the later one, which fails on top of the earlier),
            // nothing else returned, everything else landed.
            bool correct = ret.Count == pairs.Count && pairs.All(pr => ret.Contains(pr.Item2) && !ret.Contains(pr.Item1))
                && land.Count + ret.Count == nTasks;
            if (!correct) allCorrect = false;
            long runMs = runs.Sum(x => x.GetProperty("runMs").GetInt64()), waitMs = runs.Sum(x => x.GetProperty("waitMs").GetInt64());
            allRunMs.AddRange(runs.Select(x => x.GetProperty("runMs").GetInt64()));
            modes[$"failing{f}.{label}"] = new
            {
                batchSize = sz, fullSuiteRuns = runs.Count, batches = r.GetProperty("batches").GetInt32(),
                landed = land.OrderBy(x => x).ToArray(), returned = ret.OrderBy(x => x).ToArray(), expectedCulpritPairs = pairs.Select(x => $"{x.Item1}+{x.Item2}").ToArray(),
                measured = true, culpritCorrect = correct, wallSeconds = r.GetProperty("wallSeconds").GetDouble(), sumRunMs = runMs, sumWaitMs = waitMs,
                epicCommits = r.GetProperty("epicCommits").GetString(), epicLog = r.GetProperty("epicLog").EnumerateArray().Select(x => x.GetString()).ToArray(),
            };
            Console.Error.WriteLine($"[sim] f{f} {label}: runs={runs.Count} correct={correct} wall={r.GetProperty("wallSeconds").GetDouble():F0}s");
        }
    }
}

if (allRunMs.Count > 0) fullSuiteMs = allRunMs.Average(); // mean runMs over every measured gate run (all of them are full-suite runs)

// ---- slot contention: 3 concurrent 3 s jobs through a 2-slot gate ----
string cst = Path.Combine(root, "st-contention");
if (Directory.Exists(cst)) Directory.Delete(cst, true);
var procs = Enumerable.Range(0, 3).Select(_ => { var g = StartGate(cst, 2, 60, 8); Thread.Sleep(1500); return g; }).ToList(); // staggered: parallel first-time compiles of testgate.cs race
var contention = procs.Select(p => { string s = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return JsonDocument.Parse(s.Trim().Split('\n').Last(l => l.StartsWith('{'))).RootElement; }).ToList();
long cWait = contention.Sum(x => x.GetProperty("waitMs").GetInt64()), cRun = contention.Sum(x => x.GetProperty("runMs").GetInt64());
var contentionRes = new { jobs = 3, slots = 2, jobSeconds = 8, waitMs = contention.Select(x => x.GetProperty("waitMs").GetInt64()).ToArray(), slotsUsed = contention.Select(x => x.GetProperty("slot").GetInt32()).ToArray(), waitFraction = Math.Round(cWait / (double)(cWait + cRun), 2) };

// ---- crashed holder: kill the holder tree, show a new run reclaims the slot after expiry ----
string kst = Path.Combine(root, "st-crash");
if (Directory.Exists(kst)) Directory.Delete(kst, true);
var holder = StartGate(kst, 1, expiryS, 120, heartbeat: 2);
string lockFile = Path.Combine(kst, "slots", "slot-0", "owner.lock");
for (int i = 0; i < 120 && !File.Exists(Path.Combine(kst, "slots", "slot-0", "heartbeat")); i++) Thread.Sleep(500);
Thread.Sleep(3000);
bool heldBeforeKill = File.Exists(lockFile);
Process.Start(new ProcessStartInfo("taskkill", $"/PID {holder.Id} /T /F") { UseShellExecute = false, RedirectStandardOutput = true })!.WaitForExit();
holder.WaitForExit();
bool lockLeftBehind = File.Exists(lockFile);
var sw2 = Stopwatch.StartNew();
var next = StartGate(kst, 1, expiryS, 1, heartbeat: 2);
string nextOut = next.StandardOutput.ReadToEnd(); string nextErr = next.StandardError.ReadToEnd(); next.WaitForExit();
var nj = JsonDocument.Parse(nextOut.Trim().Split('\n').Last(l => l.StartsWith('{'))).RootElement;
bool reclaimed = nextErr.Contains("reclaimed stale lock") && next.ExitCode == 0;
var crash = new { expirySeconds = expiryS, heldBeforeKill, lockLeftBehindAfterKill = lockLeftBehind, newRunWaitMs = nj.GetProperty("waitMs").GetInt64(), reclaimed, slot = nj.GetProperty("slot").GetInt32() };

// ---- assemble results.json per the contract ----
object? M(string k) => modes.TryGetValue(k, out var v) ? v : null;
int RunsOf(string k) => modes.TryGetValue(k, out var v) ? (int)v.GetType().GetProperty("fullSuiteRuns")!.GetValue(v)! : -1;
double WallOf(string k) => modes.TryGetValue(k, out var v) ? (v.GetType().GetProperty("wallSeconds")?.GetValue(v) as double?) ?? -1 : -1;
var metrics = new Dictionary<string, object?>
{
    ["fullSuiteMsMeasured"] = Math.Round(fullSuiteMs), ["fullSuiteMsMin"] = allRunMs.Count > 0 ? allRunMs.Min() : 0, ["fullSuiteMsMax"] = allRunMs.Count > 0 ? allRunMs.Max() : 0, ["measuredRuns"] = allRunMs.Count,
    ["fullSuiteRuns.batched.failing0"] = RunsOf("failing0.batched"),
    ["fullSuiteRuns.batched.failing1"] = RunsOf("failing1.batched"),
    ["fullSuiteRuns.batched.failing2"] = RunsOf("failing2.batched"),
    ["modelledCostSeconds.batched.failing0"] = Math.Round(RunsOf("failing0.batched") * fullSuiteMs / 1000, 1),
    ["modelledCostSeconds.batched.failing1"] = Math.Round(RunsOf("failing1.batched") * fullSuiteMs / 1000, 1),
    ["modelledCostSeconds.batched.failing2"] = Math.Round(RunsOf("failing2.batched") * fullSuiteMs / 1000, 1),
    ["wallSeconds.batched.failing0"] = WallOf("failing0.batched"),
    ["wallSeconds.batched.failing1"] = WallOf("failing1.batched"),
    ["wallSeconds.batched.failing2"] = WallOf("failing2.batched"),
    ["culpritCorrect.all"] = allCorrect,
    ["slotUtilisation.contentionDemoWaitFraction"] = contentionRes.waitFraction,
    ["crashedLockRecovered"] = crash.reclaimed,
};
var baseline = new Dictionary<string, object?>
{
    ["fullSuiteRuns.serial.failing0"] = RunsOf("failing0.serial"),
    ["fullSuiteRuns.serial.failing1"] = RunsOf("failing1.serial"),
    ["fullSuiteRuns.serial.failing2"] = RunsOf("failing2.serial"),
    ["modelledCostSeconds.serial.failing0"] = Math.Round(RunsOf("failing0.serial") * fullSuiteMs / 1000, 1),
    ["modelledCostSeconds.serial.failing1"] = Math.Round(RunsOf("failing1.serial") * fullSuiteMs / 1000, 1),
    ["modelledCostSeconds.serial.failing2"] = Math.Round(RunsOf("failing2.serial") * fullSuiteMs / 1000, 1),
    ["wallSeconds.serial.failing0"] = WallOf("failing0.serial"),
    ["wallSeconds.serial.failing1"] = WallOf("failing1.serial"),
    ["wallSeconds.serial.failing2"] = WallOf("failing2.serial"),
};
bool batchedFewer = new[] { 0, 1, 2 }.All(f => RunsOf($"failing{f}.batched") >= 0 && RunsOf($"failing{f}.batched") < RunsOf($"failing{f}.serial"));
bool f0ok = RunsOf("failing0.batched") == (int)Math.Ceiling(12 / (double)size);
var result = new Dictionary<string, object?>
{
    ["spike"] = "01", ["variant"] = "a",
    ["question"] = "Does a fixed batch size N=" + size + " with halving bisect cut full-suite runs vs one-suite-per-task, isolate seeded culprits, and does a testgate slot lock survive a crashed holder?",
    ["metrics"] = metrics, ["baseline"] = baseline,
    ["verdict"] = allCorrect && batchedFewer && f0ok && crash.reclaimed ? "works" : "partial",
    ["notes"] = notes.Concat(new[]
    {
        "Sandbox: 6 projects x 10 tests x 100 ms, 12 tasks, seed 1; machine shared and ~100% CPU so wall seconds are noisy; counts are the reliable metric.",
        "modelledCost = fullSuiteRuns x mean runMs of all measured full-suite gate runs (includes incremental build; spread in fullSuiteMsMin/Max reflects machine load). Serial baseline is modelled as one run per task (batch size 1 == same code path), not executed, because each run took minutes under load.",
    }).ToArray(),
    ["howToRun"] = "dotnet run simulate.cs -- --root C:\\Development\\agent-swarm-wt\\s1a --size 4 --expiry-s 10",
    ["modes"] = modes, ["contentionDemo"] = contentionRes, ["crashDemo"] = crash,
};
File.WriteAllText(outPath, JsonSerializer.Serialize(result, jo));
Console.WriteLine(JsonSerializer.Serialize(result, jo));
return 0;

Process StartGate(string state, int slots, int expiry, int sleepSeconds, int heartbeat = 5)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { "run", gateCs, "--", "run", "--slots", slots.ToString(), "--state", state, "--expiry-s", expiry.ToString(), "--heartbeat-s", heartbeat.ToString(),
        "--poll-ms", "250", "--", "powershell", "-NoProfile", "-Command", $"Start-Sleep -Seconds {sleepSeconds}" }) psi.ArgumentList.Add(a);
    var p = Process.Start(psi)!;
    return p;
}
