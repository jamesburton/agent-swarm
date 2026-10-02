#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1B: adaptive batch + file-overlap pre-batching + halving bisect.
// Usage (run from this folder): dotnet run batch.cs -- <sandbox> <tasks.json> --state <dir>
//   [--mode batched|serial|measure] [--start 4 --min 2 --max 8] [--slots 2] [--epic E1] [--worktree <dir>]
//   [--overlap-map <json>] [--testgate testgate.cs] [--out summary.json]
// batched: adaptive size (x2 after green batch, /2 after red, bounded), overlapping `touches` kept in separate batches,
//          sequential merge into worktree on epic/E1 (conflict => offender rejected, rest continue), one full suite per
//          batch via testgate, halving bisect on red, squash-per-ticket onto epic/E1 with trailers when green.
// serial:  size fixed at 1 (full suite per task) = baseline.   measure: time one warm full suite on main.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

string? Opt(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (args.Length < 2 || args[0].StartsWith("--") || args[1].StartsWith("--") || Opt("state") is null)
{ Console.Error.WriteLine("usage: batch.cs -- <sandbox> <tasks.json> --state <dir> [options]"); return 2; }

string sandbox = Path.GetFullPath(args[0]);
string tasksFile = Path.GetFullPath(args[1]);
string state = Path.GetFullPath(Opt("state")!);
string mode = Opt("mode") ?? "batched";
string epic = Opt("epic") ?? "E1";
string branch = $"epic/{epic}";
int slots = int.Parse(Opt("slots") ?? "2");
int min = int.Parse(Opt("min") ?? "2"), max = int.Parse(Opt("max") ?? "8"), start = int.Parse(Opt("start") ?? "4");
if (mode == "serial") min = max = start = 1;
string wt = Path.GetFullPath(Opt("worktree") ?? sandbox + "-wt");
string testgate = Path.GetFullPath(Opt("testgate") ?? "testgate.cs");
string outFile = Opt("out") ?? Path.Combine(state, "summary.json");
Directory.CreateDirectory(Path.Combine(state, "logs"));

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
string[] Ident = ["-c", "user.name=batch", "-c", "user.email=batch@example.invalid", "-c", "core.autocrlf=false", "-c", "core.longpaths=true"];
(int code, string stdout, string stderr) Git(string dir, params string[] a) => Exec(dir, "git", Ident.Concat(a).ToArray());
string GitOk(string dir, params string[] a)
{
    var r = Git(dir, a);
    if (r.code != 0) throw new Exception($"git {string.Join(' ', a)} failed in {dir}: {r.stderr}");
    return r.stdout.Trim();
}

// ---- inputs ------------------------------------------------------------------------------------------------
var jopt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var tasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(tasksFile), jopt)!;
var overlapMap = Opt("overlap-map") is { } om
    ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(om), jopt)!   // SYNTHESIZED extra touches (demo data)
    : new Dictionary<string, string[]>();
var touches = tasks.ToDictionary(t => t.Id, t => t.Touches.Concat(overlapMap.GetValueOrDefault(t.Id) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
bool Overlaps(string a, string b) => touches[a].Overlaps(touches[b]);

// ---- worktree on epic/E1 -----------------------------------------------------------------------------------
if (Directory.Exists(Path.Combine(wt, ".git")) || File.Exists(Path.Combine(wt, ".git")))
{
    Git(sandbox, "worktree", "prune");
    GitOk(wt, "checkout", "-q", "-f", "--detach", "main");
    GitOk(sandbox, "branch", "-f", branch, "main");
    GitOk(wt, "checkout", "-q", branch);
}
else GitOk(sandbox, "worktree", "add", "-q", "-B", branch, wt, "main");

int suiteRuns = 0, bisectRuns = 0, skippedRuns = 0, batchNo = 0;
double waitMs = 0, runMs = 0;
var rejected = new List<object>();
var landed = new List<object>();
var batchLog = new List<object>();
var batchOf = new Dictionary<string, int>();

(int rc, double wait, double run) Suite(string label, bool bisect)
{
    suiteRuns++; if (bisect) bisectRuns++;
    var r = Exec(Path.GetDirectoryName(testgate)!, "dotnet", "run", testgate, "--", "run", "--slots", slots.ToString(), "--state", state,
        "--cwd", wt, "--", "dotnet", "test", "Sandbox.slnx", "--nologo", "-v", "q");
    File.WriteAllText(Path.Combine(state, "logs", $"suite-{suiteRuns:000}-{label}.log"), r.stderr + "\n" + r.stdout);
    var last = r.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.TrimStart().StartsWith('{'));
    if (last is null) return (-1, 0, 0);
    using var d = JsonDocument.Parse(last);
    double w = d.RootElement.GetProperty("waitMs").GetDouble(), ru = d.RootElement.GetProperty("runMs").GetDouble();
    waitMs += w; runMs += ru;
    return (d.RootElement.GetProperty("exitCode").GetInt32(), w, ru);
}

var sw = Stopwatch.StartNew();

if (mode == "measure")
{
    Suite("warmup", false);
    var m = Suite("measured", false);
    var o = new { mode, singleSuiteMs = m.run, waitMs = m.wait, rc = m.rc };
    File.WriteAllText(outFile, JsonSerializer.Serialize(o, jopt)); Console.WriteLine(JsonSerializer.Serialize(o));
    return m.rc == 0 ? 0 : 1;
}

void Reject(string id, string reason) { Console.Error.WriteLine($"  REJECT {id}: {reason}"); rejected.Add(new { id, reason, batch = batchOf.GetValueOrDefault(id) }); }

// Build integration state = epic tip + sequential merge of `set`. Returns the tasks that merged cleanly.
List<TaskItem> Integrate(List<TaskItem> set)
{
    GitOk(wt, "checkout", "-q", "-f", "--detach", branch);
    GitOk(wt, "reset", "-q", "--hard");
    var merged = new List<TaskItem>();
    foreach (var t in set)
    {
        var r = Git(wt, "merge", "--no-ff", "--no-edit", "-q", t.Branch);
        if (r.code != 0) { Git(wt, "merge", "--abort"); Reject(t.Id, "merge conflict (offender; back to worker)"); }
        else merged.Add(t);
    }
    return merged;
}

void Land(List<TaskItem> set, int bn)
{
    GitOk(wt, "checkout", "-q", "-f", branch);
    foreach (var t in set)
    {
        GitOk(wt, "merge", "--squash", "-q", t.Branch);
        string subject = GitOk(wt, "log", "-1", "--format=%s", t.Branch);
        GitOk(wt, "commit", "-q", "-m", subject, "-m", $"Ticket: {t.Id}\nEpic: {epic}\nBatch: {bn}");
        landed.Add(new { id = t.Id, batch = bn, commit = GitOk(wt, "rev-parse", "--short", "HEAD") });
    }
}

// Returns true if any red was seen. knownRed: this exact state was already shown red by the parent (skip the run).
bool ProcessSet(List<TaskItem> set, int bn, bool knownRed, bool bisect)
{
    var merged = Integrate(set);
    if (merged.Count == 0) return false;
    bool red;
    if (knownRed) { red = true; skippedRuns++; }
    else red = Suite($"b{bn}-{(bisect ? "bisect" : "batch")}-{string.Join('+', merged.Select(t => t.Id))}", bisect).rc != 0;
    batchLog.Add(new { batch = bn, bisect, tasks = merged.Select(t => t.Id).ToArray(), result = knownRed ? "red(inferred)" : red ? "red" : "green" });
    if (!red) { Land(merged, bn); return false; }
    if (merged.Count == 1) { Reject(merged[0].Id, "suite red on epic tip (culprit isolated)"); return true; }

    int h = merged.Count / 2;
    var left = merged.Take(h).ToList(); var right = merged.Skip(h).ToList();
    bool leftRed = ProcessSet(left, bn, false, true);
    // Left fully green and landed => epic tip + right == the parent state we already saw red: infer, don't re-run.
    ProcessSet(right, bn, !leftRed, true);
    return true;
}

// Pre-batch: take up to `size` queued tasks, skipping any whose touches overlap one already in the batch.
List<TaskItem> PreBatch(List<TaskItem> queue, int size)
{
    var pick = new List<TaskItem>();
    foreach (var t in queue)
        if (pick.Count < size && !pick.Any(p => Overlaps(p.Id, t.Id))) pick.Add(t);
    return pick;
}

var q = tasks.ToList();
int cur = start;
var sizeTrace = new List<int>();
while (q.Count > 0)
{
    var pick = PreBatch(q, cur);
    foreach (var t in pick) q.Remove(t);
    batchNo++; sizeTrace.Add(cur);
    foreach (var t in pick) batchOf[t.Id] = batchNo;
    Console.Error.WriteLine($"batch {batchNo} size {cur}: {string.Join(',', pick.Select(t => t.Id))}");
    bool red = ProcessSet(pick, batchNo, false, false);
    cur = red ? Math.Max(cur / 2, min) : Math.Min(cur * 2, max);
}
GitOk(wt, "checkout", "-q", "-f", branch);
sw.Stop();

// Overlap bookkeeping: how many overlapping pairs ended in the same / different batch (and in a naive fixed-4 chunking).
var pairs = (from a in tasks from b in tasks where string.CompareOrdinal(a.Id, b.Id) < 0 && Overlaps(a.Id, b.Id) select (a: a.Id, b: b.Id)).ToList();
int naiveSame = pairs.Count(p => tasks.FindIndex(t => t.Id == p.a) / 4 == tasks.FindIndex(t => t.Id == p.b) / 4);
var summary = new
{
    mode, tasks = tasks.Count, fullSuiteRuns = suiteRuns, bisectRuns, inferredRedSkipped = skippedRuns, batches = batchNo, sizeTrace,
    wallSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1), waitMs, runMs,
    slotUtilisation = runMs + waitMs > 0 ? Math.Round(runMs / (runMs + waitMs), 3) : 1.0,
    landed, rejected, batchLog,
    overlapPairs = pairs.Count, overlapPairsSameBatch = pairs.Count(p => batchOf[p.a] == batchOf[p.b]),
    overlapPairsSeparated = pairs.Count(p => batchOf[p.a] != batchOf[p.b]), naiveFixed4SameBatch = naiveSame,
    overlapMapSynthesized = overlapMap.Count > 0};
File.WriteAllText(outFile, JsonSerializer.Serialize(summary, jopt));
Console.WriteLine(JsonSerializer.Serialize(summary));
return 0;

record TaskItem(string Id, string[] Touches, string Branch);
