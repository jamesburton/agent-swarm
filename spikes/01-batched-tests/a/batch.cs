#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// THROWAWAY SPIKE 1A: fixed-size batches + halving bisect, squash-at-green onto epic/E1.
// Usage: dotnet run batch.cs -- <sandbox> <tasks.json> --state <dir> [--size 4] [--worktree <path>] [--slots 1]
//                               [--epic E1] [--expiry-s 60] [--out result.json]
using System.Diagnostics;
using System.Text.Json;

string sandbox = args[0], tasksPath = args[1];
string? state = null, wt = null, outPath = null; int size = 4, slots = 1, expiryS = 60; string epic = "E1";
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--state": state = args[++i]; break;
        case "--size": size = int.Parse(args[++i]); break;
        case "--worktree": wt = args[++i]; break;
        case "--slots": slots = int.Parse(args[++i]); break;
        case "--epic": epic = args[++i]; break;
        case "--expiry-s": expiryS = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}
if (state is null) { Console.Error.WriteLine("--state required"); return 2; }
Directory.CreateDirectory(state);
wt ??= Path.Combine(state, "wt");
string epicBranch = $"epic/{epic}";
// File-based apps run from a cache dir, so locate testgate.cs via env var or the working directory.
string gate = Environment.GetEnvironmentVariable("TESTGATE_CS") ?? Path.Combine(Directory.GetCurrentDirectory(), "testgate.cs");

var tasks = JsonSerializer.Deserialize<List<TaskDesc>>(File.ReadAllText(tasksPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var byId = tasks.ToDictionary(t => t.Id);
var runs = new List<object>(); var landed = new List<string>(); var returned = new List<object>();
int batchNo = 0, curBatch = 0; int fullRuns = 0;
var total = Stopwatch.StartNew();

// ---- integration worktree: created from main as epic/E1 (reused, with its obj/ caches, if it already exists) ----
if (Directory.Exists(Path.Combine(wt, ".git")) || File.Exists(Path.Combine(wt, ".git")))
{
    GitOk(wt, "checkout", "-q", "-B", epicBranch, "main");
    GitOk(wt, "reset", "-q", "--hard", "main");
}
else
{
    GitOk(sandbox, "worktree", "prune");
    GitOk(sandbox, "worktree", "add", "-q", "-B", epicBranch, wt, "main");
}
string baseSha = Git(wt, "rev-parse", "HEAD").Out.Trim();

// ---- queue -> fixed-size batches ----
var queue = new Queue<TaskDesc>(tasks);
while (queue.Count > 0)
{
    var group = new List<TaskDesc>();
    while (group.Count < size && queue.Count > 0) group.Add(queue.Dequeue());
    curBatch = ++batchNo;
    Resolve(group, knownRed: false);
}
total.Stop();

// ---- results ----
var log = Git(wt, "log", "--format=%h %s | %(trailers:key=Batch,valueonly,separator=)", "--reverse", $"{baseSha}..{epicBranch}").Out;
var summary = new
{
    epic = epicBranch, size, batches = batchNo, fullSuiteRuns = fullRuns,
    landed, returned, runs,
    wallSeconds = Math.Round(total.Elapsed.TotalSeconds, 1),
    epicCommits = Git(wt, "rev-list", "--count", $"{baseSha}..{epicBranch}").Out.Trim(),
    epicLog = log.Split('\n', StringSplitOptions.RemoveEmptyEntries),
};
string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
if (outPath != null) File.WriteAllText(outPath, json);
Console.WriteLine(json);
return 0;

// ===================== bisect core =====================
void Resolve(List<TaskDesc> group, bool knownRed)
{
    while (group.Count > 0)
    {
        bool red = knownRed;
        if (!knownRed)
        {
            var (status, offender, _) = Test(group);
            if (status == "conflict")
            {
                returned.Add(new { id = offender!.Id, reason = "merge-conflict", batch = curBatch });
                group = group.Where(t => t != offender).ToList();
                continue; // re-test the remainder
            }
            red = status == "red";
        }
        if (!red) { Land(group); return; }
        if (group.Count == 1)
        {
            returned.Add(new { id = group[0].Id, reason = "test-failure", batch = curBatch });
            return;
        }
        int half = group.Count / 2;
        var left = group.Take(half).ToList(); var right = group.Skip(half).ToList();
        int returnedBefore = returned.Count;
        Resolve(left, false);
        // If everything in the left half landed green, the right half on top of it is exactly the tree that
        // was just seen red, so it is known red without another full run.
        bool leftAllGreen = returned.Count == returnedBefore;
        Resolve(right, knownRed: leftAllGreen);
        return;
    }
}

// Merge the group sequentially on top of the epic tip (stop on first conflict) and run the full suite once via testgate.
(string Status, TaskDesc? Offender, string Tail) Test(List<TaskDesc> group)
{
    GitOk(wt, "checkout", "-q", "--detach", epicBranch); // trial merges happen on a detached HEAD, never on the epic branch
    GitOk(wt, "reset", "-q", "--hard", epicBranch);
    foreach (var t in group)
    {
        var m = Git(wt, "merge", "--no-ff", "--no-edit", "-q", t.Branch);
        if (m.Code != 0) { Git(wt, "merge", "--abort"); GitOk(wt, "reset", "-q", "--hard", epicBranch); return ("conflict", t, m.Out); }
    }
    fullRuns++;
    var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = wt, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", gate, "--", "run", "--slots", slots.ToString(), "--state", state!, "--expiry-s", expiryS.ToString(), "--", "dotnet", "test", "Sandbox.slnx", "--nologo", "-v", "q" })
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var errTask = p.StandardError.ReadToEndAsync();
    string stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    string err = errTask.Result;
    var gateJson = JsonDocument.Parse(stdout.Trim().Split('\n').Last(l => l.TrimStart().StartsWith('{'))).RootElement;
    var failed = err.Split('\n').Where(l => l.Contains("Failed ") && l.Contains('[')).Select(l => l.Trim()).Distinct().Take(5).ToArray();
    bool green = p.ExitCode == 0;
    runs.Add(new { batch = curBatch, tasks = group.Select(t => t.Id).ToArray(), result = green ? "green" : "red",
        waitMs = gateJson.GetProperty("waitMs").GetInt64(), runMs = gateJson.GetProperty("runMs").GetInt64(), failed });
    Console.Error.WriteLine($"[batch] run {fullRuns}: {string.Join("+", group.Select(t => t.Id))} -> {(green ? "green" : "red")} ({gateJson.GetProperty("runMs").GetInt64()} ms)");
    return (green ? "green" : "red", null, "");
}

// Squash each ticket to ONE commit onto epic/E1 (queue order), then check the tree equals the tested tree.
void Land(List<TaskDesc> group)
{
    string testedTree = Git(wt, "rev-parse", "HEAD^{tree}").Out.Trim();
    GitOk(wt, "checkout", "-q", epicBranch);
    GitOk(wt, "reset", "-q", "--hard", epicBranch);
    foreach (var t in group)
    {
        GitOk(wt, "merge", "--squash", "-q", t.Branch);
        string subject = Git(wt, "log", "-1", "--format=%s", t.Branch).Out.Trim();
        if (subject.StartsWith(t.Id + ":")) subject = subject[(t.Id.Length + 1)..].Trim();
        GitOk(wt, "commit", "-q",
            "-m", $"{t.Id}: {subject}", "-m", $"Ticket: {t.Id}\nEpic: {epic}\nBatch: {curBatch}");
        // Move the epic branch to the new commit (the worktree has epic/E1 checked out, so HEAD is the branch).
        landed.Add(t.Id);
    }
    string landedTree = Git(wt, "rev-parse", "HEAD^{tree}").Out.Trim();
    if (landedTree != testedTree) { Console.Error.WriteLine($"[batch] WARNING squashed tree != tested tree for {string.Join(",", group.Select(t => t.Id))}"); }
}

static (int Code, string Out) Git(string dir, params string[] a)
{
    var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var x in new[] { "-c", "user.name=swarm-batch", "-c", "user.email=batch@example.invalid" }.Concat(a)) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    var e = p.StandardError.ReadToEndAsync();
    string o = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, o + e.Result);
}

static void GitOk(string dir, params string[] a)
{
    var r = Git(dir, a);
    if (r.Code != 0) throw new Exception($"git {string.Join(' ', a)} (in {dir}) failed: {r.Out}");
}

record TaskDesc(string Id, string[] Touches, string Branch);
