#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1B: adaptive batch + derived-touches pre-batching + halving bisect + conflict return/rebase.
// Usage (run from this folder): dotnet run batch.cs -- <sandbox> <tasks.json> --state <dir>
//   [--mode batched|serial|measure] [--start 4 --min 2 --max 8] [--fixed N] [--no-prebatch] [--slots 2] [--epic E1]
//   [--worktree <dir>] [--base main] [--testgate testgate.cs] [--expiry-sec 60] [--heartbeat-sec 5] [--run-id <name>] [--out summary.json]
// batched: adaptive size (x2 after green batch, /2 after red, bounded). Each task's `touches` is DERIVED from
//          `git diff --name-only <base>...<task branch>` (tasks.json `touches` is ignored); overlapping tasks are kept in
//          separate batches (unless --no-prebatch). Sequential merge into a worktree on epic/E1; first conflict = offender:
//          merge aborted, entry written to returned.json (task, conflictingWith, files, git output), rest of the batch
//          continues; after the batch the returned task is `git rebase`d onto the epic tip: clean => re-queued (once),
//          conflict again => needs-worker. One full suite per batch via testgate, halving bisect on red, squash-per-ticket
//          onto epic/E1 with trailers when green.
// serial:  size fixed at 1 (full suite per task, each task on top of the previous green state) = baseline.
// measure: time one warm full suite on main.   --fixed N: min=max=start=N (naive fixed chunks).
// State layout: <state>/slots/ (shared lock files, see testgate.cs) and <state>/runs/<run-id>/{logs,summary.json,returned.json}.
// Exit codes: 0 all tasks landed, 1 ran fine but some tasks returned/rejected, 2 usage/bad option, 3 bad input (tasks.json,
//             missing branch/base), 4 environment failure (git, testgate, worktree). Errors are ONE line on stderr: "error: ...".
using System.Diagnostics;
using System.Text;
using System.Text.Json;

string? Opt(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => Array.IndexOf(args, "--" + name) >= 0;
if (args.Length < 2 || args[0].StartsWith("--") || args[1].StartsWith("--") || Opt("state") is null)
{ Console.Error.WriteLine("usage: batch.cs -- <sandbox> <tasks.json> --state <dir> [options]"); return 2; }

try
{
    return RunMain();
}
catch (UserError e) { Console.Error.WriteLine("error: " + e.Message.ReplaceLineEndings(" ")); return e.Code; }
catch (Exception e) { Console.Error.WriteLine("error: " + e.Message.ReplaceLineEndings(" ")); return 4; }

int RunMain()
{
    string sandbox = Path.GetFullPath(args[0]);
    string tasksFile = Path.GetFullPath(args[1]);
    string state = Path.GetFullPath(Opt("state")!);
    if (state.Length > 200) throw new UserError($"state dir path is {state.Length} chars (limit 200): Windows MAX_PATH breaks git/dotnet children, use a shorter --state", 2);
    string mode = Opt("mode") ?? "batched";
    if (mode is not ("batched" or "serial" or "measure")) throw new UserError($"unknown --mode '{mode}' (batched|serial|measure)", 2);
    string epic = Opt("epic") ?? "E1";
    string branch = $"epic/{epic}";
    string baseRef = Opt("base") ?? "main";
    int slots = int.Parse(Opt("slots") ?? "2");
    int min = int.Parse(Opt("min") ?? "2"), max = int.Parse(Opt("max") ?? "8"), start = int.Parse(Opt("start") ?? "4");
    if (Opt("fixed") is { } fx) min = max = start = int.Parse(fx);
    if (mode == "serial") min = max = start = 1;
    bool prebatch = mode != "serial" && !Flag("no-prebatch");
    string wt = Path.GetFullPath(Opt("worktree") ?? sandbox + "-wt");
    string rbWt = wt + "-rb"; // second worktree used only for `git rebase` of returned tasks
    string testgate = Path.GetFullPath(Opt("testgate") ?? "testgate.cs");
    string runId = Opt("run-id") ?? Path.GetFileName(sandbox.TrimEnd('\\', '/'));
    string runDir = Path.Combine(state, "runs", runId);
    string outFile = Opt("out") ?? Path.Combine(runDir, "summary.json");
    string returnedFile = Path.Combine(runDir, "returned.json");
    Directory.CreateDirectory(Path.Combine(runDir, "logs"));

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
        if (r.code != 0) throw new UserError($"git {string.Join(' ', a)} failed in {dir}: {r.stderr.Trim()}", 4);
        return r.stdout.Trim();
    }
    string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- inputs + preflight (before any worktree is touched) ---------------------------------------------------
    var jopt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    if (!Directory.Exists(sandbox) || Git(sandbox, "rev-parse", "--git-dir").code != 0) throw new UserError($"sandbox '{sandbox}' is not a git repository", 3);
    if (!File.Exists(tasksFile)) throw new UserError($"tasks file '{tasksFile}' not found", 3);
    List<TaskItem> tasks;
    try { tasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(tasksFile), jopt) ?? []; }
    catch (JsonException e) { throw new UserError($"tasks file is not valid JSON: {e.Message}", 3); }

    if (tasks.Count == 0)
    {
        Console.Error.WriteLine("nothing to do: tasks.json is empty (0 tasks); no worktree created, no suite run");
        var empty = new { mode, tasks = 0, fullSuiteRuns = 0, tasksLanded = 0, returned = 0, rebasedAndLanded = 0, needsWorker = 0, note = "empty batch" };
        File.WriteAllText(outFile, JsonSerializer.Serialize(empty, jopt)); Console.WriteLine(JsonSerializer.Serialize(empty));
        return 0;
    }
    var missing = tasks.Where(t => Git(sandbox, "rev-parse", "--verify", "--quiet", $"refs/heads/{t.Branch}").code != 0).Select(t => $"{t.Id} ({t.Branch})").ToList();
    if (missing.Count > 0) throw new UserError($"task branch not found: {string.Join(", ", missing)}; nothing was merged (fix tasks.json or create the branch)", 3);
    if (Git(sandbox, "rev-parse", "--verify", "--quiet", $"refs/heads/{baseRef}").code != 0) throw new UserError($"base branch '{baseRef}' not found in sandbox", 3);

    // Touches are DERIVED from the branches (tasks.json `touches` is deliberately not read).
    HashSet<string> Derive(TaskItem t, string from) => Lines(GitOk(sandbox, "diff", "--name-only", $"{from}...{t.Branch}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var touches = tasks.ToDictionary(t => t.Id, t => Derive(t, baseRef));
    var touches0 = touches.ToDictionary(kv => kv.Key, kv => kv.Value); // as first derived, for the overlap statistics
    bool Overlaps(string a, string b) => touches[a].Overlaps(touches[b]);

    // ---- worktrees on epic/E1 ------------------------------------------------------------------------------------
    if (Directory.Exists(Path.Combine(wt, ".git")) || File.Exists(Path.Combine(wt, ".git")))
    {
        Git(sandbox, "worktree", "prune");
        GitOk(wt, "checkout", "-q", "-f", "--detach", baseRef);
        GitOk(sandbox, "branch", "-f", branch, baseRef);
        GitOk(wt, "checkout", "-q", branch);
    }
    else GitOk(sandbox, "worktree", "add", "-q", "-B", branch, wt, baseRef);

    int suiteRuns = 0, bisectRuns = 0, skippedRuns = 0, batchNo = 0;
    double waitMs = 0, runMs = 0;
    var rejected = new List<object>();
    var landed = new List<object>();
    var batchLog = new List<object>();
    var batchOf = new Dictionary<string, int>();
    var landedIds = new List<string>();
    var returned = new List<ReturnedItem>();
    var attempts = new Dictionary<string, int>();
    var rejectedIds = new HashSet<string>();
    var suites = new List<object>();

    (int rc, double wait, double run) Suite(string label, bool bisect)
    {
        suiteRuns++; if (bisect) bisectRuns++;
        var r = Exec(Path.GetDirectoryName(testgate)!, "dotnet", "run", testgate, "--", "run", "--slots", slots.ToString(), "--state", state,
            "--cwd", wt, "--expiry-sec", Opt("expiry-sec") ?? "60", "--heartbeat-sec", Opt("heartbeat-sec") ?? "5", "--", "dotnet", "test", "Sandbox.slnx", "--nologo", "-v", "q");
        File.WriteAllText(Path.Combine(runDir, "logs", $"suite-{suiteRuns:000}-{label}.log"), r.stderr + "\n" + r.stdout);
        var last = r.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.TrimStart().StartsWith('{'));
        if (last is null) throw new UserError($"testgate returned no result (exit {r.code}): {Lines(r.stderr).LastOrDefault() ?? "no output"}", 4);
        using var d = JsonDocument.Parse(last);
        double w = d.RootElement.GetProperty("waitMs").GetDouble(), ru = d.RootElement.GetProperty("runMs").GetDouble();
        waitMs += w; runMs += ru;
        suites.Add(new { label, waitMs = w, runMs = ru, acquiredUtc = d.RootElement.GetProperty("acquiredUtc").GetDateTime(), releasedUtc = d.RootElement.GetProperty("releasedUtc").GetDateTime(), slot = d.RootElement.GetProperty("slot").GetInt32(), reclaimed = d.RootElement.GetProperty("reclaimed").GetBoolean() });
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

    void Reject(TaskItem t, string reason)
    {
        Console.Error.WriteLine($"  REJECT {t.Id}: {reason}");
        rejectedIds.Add(t.Id); rejected.Add(new { id = t.Id, reason, batch = batchOf.GetValueOrDefault(t.Id) });
        returned.Add(new ReturnedItem { Task = t.Id, Kind = "red", Stage = "suite", Batch = batchOf.GetValueOrDefault(t.Id), ConflictingWith = [], Files = [], GitOutput = reason, Rebase = "n/a (red: worker must fix)", Final = "returned-red" });
    }

    // Stop-on-conflict: name the offender and the already-in-state task(s) it collides with, return it to its worker.
    void ReturnConflict(TaskItem t, string stage, string gitOutput, string[] files, IEnumerable<string> inState)
    {
        var partners = inState.Where(id => id != t.Id && touches[id].Overlaps(files)).Distinct().ToArray();
        Console.Error.WriteLine($"  CONFLICT {t.Id} vs {(partners.Length > 0 ? string.Join('+', partners) : "epic tip")} in {string.Join(',', files)} ({stage}): returned to worker");
        returned.Add(new ReturnedItem { Task = t.Id, Kind = "conflict", Stage = stage, Batch = batchNo, ConflictingWith = partners, Files = files, GitOutput = gitOutput.Trim(), Rebase = "pending" });
    }

    // Build integration state = epic tip + sequential merge of `set`. Returns the tasks that merged cleanly.
    List<TaskItem> Integrate(List<TaskItem> set)
    {
        GitOk(wt, "checkout", "-q", "-f", "--detach", branch);
        GitOk(wt, "reset", "-q", "--hard");
        var merged = new List<TaskItem>();
        foreach (var t in set)
        {
            var r = Git(wt, "merge", "--no-ff", "--no-edit", t.Branch);
            if (r.code != 0)
            {
                var files = Lines(Git(wt, "diff", "--name-only", "--diff-filter=U").stdout);
                Git(wt, "merge", "--abort");
                ReturnConflict(t, "merge", r.stdout + r.stderr, files, landedIds.Concat(merged.Select(m => m.Id)));
            }
            else merged.Add(t);
        }
        return merged;
    }

    void Land(List<TaskItem> set, int bn)
    {
        GitOk(wt, "checkout", "-q", "-f", branch);
        foreach (var t in set)
        {
            var sq = Git(wt, "merge", "--squash", t.Branch);
            if (sq.code != 0)
            {
                // e.g. a stacked task whose parent already squash-landed: tested merged, but the squash cannot replay.
                var files = Lines(Git(wt, "diff", "--name-only", "--diff-filter=U").stdout);
                Git(wt, "reset", "-q", "--hard");
                ReturnConflict(t, "land", sq.stdout + sq.stderr, files, landedIds);
                continue;
            }
            string subject = GitOk(wt, "log", "-1", "--format=%s", t.Branch);
            var cm = Git(wt, "commit", "-q", "-m", subject, "-m", $"Ticket: {t.Id}\nEpic: {epic}\nBatch: {bn}");
            if (cm.code != 0) { Git(wt, "reset", "-q", "--hard"); Reject(t, "squash produced no commit (change already on epic)"); continue; }
            landedIds.Add(t.Id);
            landed.Add(new { id = t.Id, batch = bn, commit = GitOk(wt, "rev-parse", "--short", "HEAD") });
        }
    }

    // Returns true if any red was seen. knownRed: this exact state was already shown red by the parent (skip the run).
    bool ProcessSet(List<TaskItem> set, int bn, bool knownRed, bool bisect)
    {
        var merged = Integrate(set);
        if (merged.Count == 0) { batchLog.Add(new { batch = bn, bisect, tasks = Array.Empty<string>(), result = "all merges conflicted: no suite run" }); return false; }
        bool red;
        if (knownRed) { red = true; skippedRuns++; }
        else red = Suite($"b{bn}-{(bisect ? "bisect" : "batch")}-{string.Join('+', merged.Select(t => t.Id))}", bisect).rc != 0;
        batchLog.Add(new { batch = bn, bisect, tasks = merged.Select(t => t.Id).ToArray(), result = knownRed ? "red(inferred)" : red ? "red" : "green" });
        if (!red) { Land(merged, bn); return false; }
        if (merged.Count == 1) { Reject(merged[0], "suite red on epic tip (culprit isolated)"); return true; }

        int h = merged.Count / 2;
        var left = merged.Take(h).ToList(); var right = merged.Skip(h).ToList();
        bool leftRed = ProcessSet(left, bn, false, true);
        // Left fully green and landed => epic tip + right == the parent state we already saw red: infer, don't re-run.
        ProcessSet(right, bn, !leftRed, true);
        return true;
    }

    // After a batch: rebase each conflict-returned task onto the epic tip. Clean => requeue (once); conflict => needs-worker.
    void RebaseReturned(List<TaskItem> queue)
    {
        foreach (var r in returned.Where(x => x.Kind == "conflict" && x.Rebase == "pending").ToList())
        {
            var t = tasks.First(x => x.Id == r.Task);
            if (attempts.GetValueOrDefault(t.Id) >= 1) { r.Rebase = "skipped (already rebased once)"; r.Final = "needs-worker"; continue; }
            attempts[t.Id] = 1;
            string tip = GitOk(wt, "rev-parse", branch);
            if (Directory.Exists(rbWt)) GitOk(rbWt, "checkout", "-q", "-f", "--detach", tip);
            else GitOk(sandbox, "worktree", "add", "-q", "--detach", rbWt, tip);
            // Rebase a COPY (rebased/<epic>/<task>), never the worker's task branch: the run stays repeatable and the worker keeps their ref.
            string rbBranch = $"rebased/{epic}/{t.Id}";
            GitOk(rbWt, "checkout", "-q", "-f", "-B", rbBranch, t.Branch);
            var rb = Git(rbWt, "rebase", tip);
            if (rb.code != 0)
            {
                r.RebaseOutput = (rb.stdout + rb.stderr).Trim();
                Git(rbWt, "rebase", "--abort");
                Git(rbWt, "checkout", "-q", "-f", "--detach", tip);
                Git(sandbox, "branch", "-D", rbBranch);
                r.Rebase = "conflict"; r.Final = "needs-worker";
                Console.Error.WriteLine($"  REBASE {t.Id}: conflicts again -> needs-worker");
                continue;
            }
            Git(rbWt, "checkout", "-q", "-f", "--detach", tip); // release the branch
            var t2 = t with { Branch = rbBranch };
            touches[t.Id] = Derive(t2, tip); // the copy contains the epic: diff against the tip it was rebased onto
            if (touches[t.Id].Count == 0) { r.Rebase = "clean, but nothing left to land"; r.Final = "no-op-after-rebase"; continue; }
            r.Rebase = $"clean (requeued as {rbBranch})";
            queue.Insert(0, t2);
            Console.Error.WriteLine($"  REBASE {t.Id}: clean -> requeued");
        }
    }

    // Pre-batch: take up to `size` queued tasks, skipping any whose derived touches overlap one already in the batch.
    List<TaskItem> PreBatch(List<TaskItem> queue, int size)
    {
        var pick = new List<TaskItem>();
        foreach (var t in queue)
            if (pick.Count < size && (!prebatch || !pick.Any(p => Overlaps(p.Id, t.Id)))) pick.Add(t);
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
        RebaseReturned(q);
        cur = red ? Math.Max(cur / 2, min) : Math.Min(cur * 2, max);
    }
    GitOk(wt, "checkout", "-q", "-f", branch);
    sw.Stop();

    // Final per-task status for returned conflicts.
    foreach (var r in returned.Where(x => x.Kind == "conflict"))
        if (landedIds.Contains(r.Task)) r.Final = r.Rebase.StartsWith("clean") ? "rebased-and-landed" : "landed";
        else if (r.Final == "") r.Final = rejectedIds.Contains(r.Task) ? "rebased-then-rejected" : "unlanded";
    File.WriteAllText(returnedFile, JsonSerializer.Serialize(returned, jopt));

    // Overlap bookkeeping (derived touches): overlapping pairs in same/different batch of the FINAL landing, and in naive fixed-4 chunks.
    var pairs = (from a in tasks from b in tasks where string.CompareOrdinal(a.Id, b.Id) < 0 && touches0[a.Id].Overlaps(touches0[b.Id]) select (a: a.Id, b: b.Id)).ToList();
    int naiveSame = pairs.Count(p => tasks.FindIndex(t => t.Id == p.a) / 4 == tasks.FindIndex(t => t.Id == p.b) / 4);
    var conflicts = returned.Where(x => x.Kind == "conflict").ToList();
    var summary = new
    {
        mode, prebatch, tasks = tasks.Count, fullSuiteRuns = suiteRuns, bisectRuns, inferredRedSkipped = skippedRuns, batches = batchNo, sizeTrace,
        wallSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1), waitMs, runMs,
        slotUtilisation = runMs + waitMs > 0 ? Math.Round(runMs / (runMs + waitMs), 3) : 1.0,
        tasksLanded = landed.Count,
        returned = conflicts.Count,
        rebasedAndLanded = conflicts.Count(x => x.Final == "rebased-and-landed"),
        needsWorker = conflicts.Count(x => x.Final == "needs-worker"),
        rejectedRed = rejected.Count,
        conflictsHit = conflicts.Count,
        landed, rejected, returnedFile, batchLog, suites,
        overlapPairs = pairs.Count, overlapPairsSameBatchFirstPlacement = pairs.Count(p => batchOf[p.a] == batchOf[p.b]),
        overlapPairsSeparated = pairs.Count(p => batchOf[p.a] != batchOf[p.b]), naiveFixed4SameBatch = naiveSame,
        touchesDerivedFromGit = true, derivedTouches = touches0.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToArray()),
    };
    File.WriteAllText(outFile, JsonSerializer.Serialize(summary, jopt));
    Console.WriteLine(JsonSerializer.Serialize(summary));
    return landed.Count == tasks.Count ? 0 : 1;
}

record TaskItem(string Id, string[] Touches, string Branch);

class ReturnedItem
{
    public string Task { get; set; } = "";
    public string Kind { get; set; } = "";          // conflict | red
    public string Stage { get; set; } = "";         // merge | land | suite
    public int Batch { get; set; }
    public string[] ConflictingWith { get; set; } = [];
    public string[] Files { get; set; } = [];
    public string GitOutput { get; set; } = "";
    public string Rebase { get; set; } = "";        // pending | clean (requeued as rebased/E/T) | conflict | ...
    public string RebaseOutput { get; set; } = "";
    public string Final { get; set; } = "";         // rebased-and-landed | needs-worker | returned-red | ...
}

class UserError : Exception
{
    public int Code { get; }
    public UserError(string message, int code) : base(message) { Code = code; }
}
