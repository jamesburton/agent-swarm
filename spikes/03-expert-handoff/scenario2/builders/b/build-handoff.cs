// PATCHED COPY for scenario2: no QuotaKit/DST-specific text; notes and diff are derived from the transcript.
// THROWAWAY SPIKE. Usage: dotnet run build-handoff.cs -- <scenarioDir> <outDir>
// Deterministic (no LLM): builds <outDir>/.handoff/{NOTES.md,last-test-run.txt,attempts.diff,worker-transcript.md}
// and <outDir>/handoff-prompt.md from the scenario's worker-transcript.md and visible tests.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2) { Console.Error.WriteLine("usage: build-handoff.cs -- <scenarioDir> <outDir>"); return 2; }
var scenario = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
var hand = Path.Combine(outDir, ".handoff");
Directory.CreateDirectory(hand);
var sw = Stopwatch.StartNew();
var testProjName = Directory.GetDirectories(scenario, "*.Tests").Select(Path.GetFileName).First()!;
var libProjName = testProjName[..^".Tests".Length];

// 1. Transcript copy + attempt parsing (1-based line numbers into the copy).
var lines = File.ReadAllLines(Path.Combine(scenario, "worker-transcript.md"));
File.WriteAllLines(Path.Combine(hand, "worker-transcript.md"), lines);
var heads = new List<(int Line, int N, string Title)>();
var statusLine = lines.Length + 1;
for (var i = 0; i < lines.Length; i++)
{
    var m = Regex.Match(lines[i], @"^## Attempt (\d+): (.*)$");
    if (m.Success) heads.Add((i + 1, int.Parse(m.Groups[1].Value), m.Groups[2].Value));
    else if (lines[i].StartsWith("## Status")) statusLine = i + 1;
}

// Text of the first line starting with the prefix inside an attempt section (lines are 1-based; section = [s, e)).
string Field(int s, int e, string prefix) =>
    lines.Skip(s).Take(e - s - 1).FirstOrDefault(l => l.StartsWith(prefix))?[prefix.Length..] ?? "";

string First(string text) // first sentence, trimmed for a one-liner
{
    text = Regex.Replace(text, @"\s+", " ").Trim();
    var m = Regex.Match(text, @"^.*?[.;](?=\s|$)");
    var s = m.Success ? m.Value : text;
    return s.Length > 170 ? s[..167] + "..." : s;
}

// Sentence of the worker's notes that explains the failure (first sentence mentioning a failure keyword).
string Why(string notes)
{
    var m = Regex.Match(notes, @"[^.]*(?:fail|breaks|off|wrong|same|contradict|late)[^.]*\.", RegexOptions.IgnoreCase);
    return First(m.Success ? m.Value : notes);
}

var transcriptText = string.Join("\n", lines);
var task = Regex.Match(transcriptText, "Task given to worker: \"(.*)\"").Groups[1].Value;
var believed = Regex.Match(transcriptText, @"Believed facts: (.*)").Groups[1].Value.Trim();
var question = Regex.Match(transcriptText, @"Open question for the expert: (.*)").Groups[1].Value.Trim();
var sb = new StringBuilder();
sb.AppendLine("# Worker notes (escalation hand-off)");
sb.AppendLine();
sb.AppendLine($"Task: {task}");
sb.AppendLine("Status: STUCK. Working tree is clean at baseline (all attempts reverted). Raw test output: `last-test-run.txt`. Code of attempts: `attempts.diff`.");
sb.AppendLine();
sb.AppendLine("## Believed facts");
sb.AppendLine($"- {believed} Evidence: worker-transcript.md:{heads[0].Line}-{heads[1].Line - 1}, :{statusLine}-{lines.Length}");
sb.AppendLine("- Every change that makes the failing tests pass breaks other tests, and vice versa (see Failed attempts).");
sb.AppendLine();
sb.AppendLine("## Failed attempts (do NOT repeat)");
for (var i = 1; i < heads.Count; i++)
{
    var (ln, n, title) = heads[i];
    var end = i + 1 < heads.Count ? heads[i + 1].Line : statusLine;
    var shortTitle = Regex.Replace(title, @"\s*\([A-Z][^)]*\)\s*$", "");
    var tag = title.Contains("TEMPTING") ? " [TEMPTING WRONG FIX]" : "";
    var idea = Field(ln, end, "Idea: ");
    var notes = Field(ln, end, "Worker notes: ");
    sb.AppendLine($"- Attempt {n} ({shortTitle}){tag}: {(idea.Length > 0 ? First(idea) : shortTitle + ".")} Failed: {Why(notes)} Evidence: worker-transcript.md:{ln}-{end - 1}");
}
sb.AppendLine();
sb.AppendLine("## Open question");
sb.AppendLine($"- {question} Evidence: worker-transcript.md:{statusLine}-{lines.Length}");
File.WriteAllText(Path.Combine(hand, "NOTES.md"), sb.ToString());

// 2. attempts.diff reconstructed from the transcript's recorded code (hunks per attempt).
var diff = new StringBuilder();
diff.AppendLine("# Cumulative record of everything tried (all reverted). Reconstructed from worker-transcript.md; hunks are against baseline.");
diff.AppendLine();
for (var k = 1; k < heads.Count; k++)
{
    var h = heads[k];
    var end = k + 1 < heads.Count ? heads[k + 1].Line : statusLine;
    diff.AppendLine();
    diff.AppendLine($"# Attempt {h.N} (worker-transcript.md:{h.Line})");
    var inCs = false;
    var any = false;
    for (var i2 = h.Line; i2 < end - 1; i2++)
    {
        if (lines[i2].StartsWith("```csharp")) { inCs = true; continue; }
        if (inCs && lines[i2].StartsWith("```")) { inCs = false; continue; }
        if (inCs) { diff.AppendLine("+ " + lines[i2]); any = true; }
    }

    if (!any) { diff.AppendLine("# no code recorded"); }
}

File.WriteAllText(Path.Combine(hand, "attempts.diff"), diff.ToString());

// 3. Raw `dotnet test` of the visible tests, run in a throwaway copy (inside the repo so global.json applies).
var tmp = Path.Combine(Path.GetDirectoryName(scenario)!, ".build-tmp-" + Guid.NewGuid().ToString("N")[..8]);
var sep = Path.DirectorySeparatorChar;
try
{
    foreach (var proj in new[] { libProjName, testProjName })
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(scenario, proj), "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}")))
        {
            var dest = Path.Combine(tmp, Path.GetRelativePath(scenario, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    var psi = new ProcessStartInfo("dotnet", $"test {testProjName}") { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true };
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync();
    var e = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    File.WriteAllText(Path.Combine(hand, "last-test-run.txt"), $"$ dotnet test {testProjName} (exit {p.ExitCode})\n{await o}{await e}");
}
finally
{
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
}

// 4. The one-paragraph prompt.
var prompt = $"You are the escalation expert. A cheaper worker got stuck on this task: \"{task}\" " +
    $"Repo root: the current directory; run the tests with `dotnet test {testProjName}`. The worker's state is in `.handoff/`: read `.handoff/NOTES.md` first (running notes; every failed attempt is one line with a pointer to evidence), then open whatever evidence it points to " +
    "(`last-test-run.txt`, `attempts.diff`, `worker-transcript.md`). Do not repeat a failed attempt listed there. Fix the library (never delete or weaken tests), make all tests pass, and reply with the root cause and the change you made.\n";
File.WriteAllText(Path.Combine(outDir, "handoff-prompt.md"), prompt);

var files = Directory.GetFiles(hand).OrderBy(x => x).ToList();
Console.WriteLine($"promptChars={prompt.Length} promptWords={prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} promptTokensApprox={prompt.Length / 4}");
foreach (var f in files)
{
    var c = File.ReadAllText(f).Length;
    Console.WriteLine($"{Path.GetFileName(f)} chars={c} tokensApprox={c / 4}");
}

Console.WriteLine($"notesFolderTokensApprox={files.Sum(f => File.ReadAllText(f).Length) / 4}");
Console.WriteLine($"buildTimeSeconds={sw.Elapsed.TotalSeconds:F1}");
return 0;
