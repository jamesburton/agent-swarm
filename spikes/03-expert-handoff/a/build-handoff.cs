// Deterministic hand-off builder (no LLM). Usage: dotnet run build-handoff.cs -- <scenarioDir> <outFile>
// Fills handoff.md (next to this script) from worker-transcript.md, `dotnet test` output and the project files.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length < 2) { Console.Error.WriteLine("usage: build-handoff.cs -- <scenarioDir> <outFile>"); return 2; }

var sw = Stopwatch.StartNew();
var scenario = Path.GetFullPath(args[0]);
var outFile = Path.GetFullPath(args[1]);
var scriptPath = AppContext.GetData("EntryPointFilePath") as string;
var scriptDir = scriptPath is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(scriptPath)!;
var template = File.ReadAllText(Path.Combine(scriptDir, "handoff.md")).Replace("\r\n", "\n");
var transcript = File.ReadAllText(Path.Combine(scenario, "worker-transcript.md")).Replace("\r\n", "\n");

// ---- Goal: the task line given to the worker.
var taskMatch = Regex.Match(transcript, "Task given to worker: \"(.*)\"");
var task = taskMatch.Success ? taskMatch.Groups[1].Value : "(task line not found)";

// ---- Current state: run the visible tests, parse the TRX.
var testProj = Path.Combine(scenario, "QuotaKit.Tests");
var trxDir = Path.Combine(Path.GetTempPath(), "handoff-trx-" + Guid.NewGuid().ToString("N"));
var psi = new ProcessStartInfo("dotnet", $"test \"{testProj}\" --nologo --logger \"trx;LogFileName=r.trx\" --results-directory \"{trxDir}\"")
{ RedirectStandardOutput = true, RedirectStandardError = true };
using (var p = Process.Start(psi)!)
{
    var o = p.StandardOutput.ReadToEndAsync();
    var e = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    Task.WaitAll(o, e);
}

var state = new StringBuilder();
var trx = Path.Combine(trxDir, "r.trx");
if (File.Exists(trx))
{
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var doc = XDocument.Load(trx);
    var c = doc.Descendants(ns + "Counters").First();
    state.AppendLine($"`dotnet test` on visible tests: {c.Attribute("failed")!.Value} failed, {c.Attribute("passed")!.Value} passed, {c.Attribute("total")!.Value} total.");
    state.AppendLine("Run: `dotnet test QuotaKit.Tests` from the scenario root.");
    state.AppendLine("Failing tests:");
    foreach (var r in doc.Descendants(ns + "UnitTestResult").Where(r => (string?)r.Attribute("outcome") == "Failed").OrderBy(r => (string?)r.Attribute("testName"), StringComparer.Ordinal))
    {
        var msg = r.Descendants(ns + "Message").FirstOrDefault()?.Value ?? "";
        var lines = msg.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
        state.AppendLine($"- `{(string?)r.Attribute("testName")}`: {string.Join(" | ", lines.Take(4))}");
    }
}
else
{
    state.AppendLine("`dotnet test` produced no TRX (build/restore failure); run it manually.");
}

try { Directory.Delete(trxDir, true); } catch { }

// ---- Files that matter: project .cs files with the first <summary> line (tests get a fixed label).
var sep = Path.DirectorySeparatorChar;
var files = new StringBuilder();
foreach (var f in Directory.EnumerateFiles(scenario, "*.cs", SearchOption.AllDirectories)
             .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}hidden{sep}"))
             .Select(f => Path.GetRelativePath(scenario, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
{
    var text = File.ReadAllText(Path.Combine(scenario, f));
    var sm = Regex.Match(text, @"/// <summary>(.*?)</summary>", RegexOptions.Singleline);
    var why = f.Contains("Tests/")
        ? "xUnit tests; the 2 failing ones are under the `seeded bug` comment; do not delete tests"
        : sm.Success ? Regex.Replace(sm.Groups[1].Value, @"\s+", " ").Trim() : "source";
    files.AppendLine($"- `{f}`: {why}");
}

// ---- Failed attempts: parse "## Attempt N: title (STATUS)" blocks.
var attempts = new StringBuilder();
var blocks = Regex.Split(transcript, @"(?m)^## ").Where(b => b.StartsWith("Attempt")).ToList();
foreach (var b in blocks)
{
    var head = b.Split('\n')[0];
    var hm = Regex.Match(head, @"^Attempt (\d+): (.*?)(?: \(([A-Z][^()]*)\))?$");
    if (!hm.Success || hm.Groups[1].Value == "0") { continue; } // attempt 0 is the baseline, covered in Current state
    var idea = Regex.Match(b, @"(?m)^Idea: (.*)$").Groups[1].Value;
    var code = Regex.Match(b, "```csharp\n(.*?)```", RegexOptions.Singleline).Groups[1].Value.Trim().Replace("\n", " ");
    var evidence = Regex.Matches(b, @"(?m)^(Failed [^\n]+|\s+Expected: [^\n]+|\s+Actual: [^\n]+)$").Select(m => m.Value.Trim()).ToList();
    var notes = Regex.Match(b, @"Worker notes: (.*?)(?=\n\n|\n## |$)", RegexOptions.Singleline).Groups[1].Value.Replace("\n", " ").Trim();
    var tag = hm.Groups[3].Success ? $" [{hm.Groups[3].Value}]" : "";
    attempts.AppendLine($"{hm.Groups[1].Value}. **{hm.Groups[2].Value}**{tag}");
    attempts.AppendLine($"   - Tried: {idea}{(code.Length > 0 ? $" `{code}`" : "")}");
    attempts.AppendLine($"   - Why it failed: {notes}");
    if (evidence.Count > 0) { attempts.AppendLine($"   - Evidence: {string.Join("; ", evidence.Take(4))}"); }
}

// ---- Rejected hypotheses: what the attempts disproved, plus the worker's own unverified belief.
var believed = Regex.Match(transcript, @"Believed facts: (.*)").Groups[1].Value.Trim();
var rejected = new StringBuilder();
rejected.AppendLine("- Test data/expectations are wrong (attempt 1): no, the events are on the previous local day; do not edit tests.");
rejected.AppendLine("- Fix by testing whether the instant is in DST, or by using a zone-constant offset (attempts 2, 3): no, the right offset belongs to the midnight being resolved.");
rejected.AppendLine("- Fix by doing the arithmetic in UTC with the offset at `instant` (attempt 4): no, same bug in different spelling.");
rejected.AppendLine($"- Worker's belief (unverified): {believed}");

// ---- Open question, constraints, expected result.
var question = Regex.Match(transcript, @"Open question for the expert: (.*)").Groups[1].Value.Trim();
var touched = Regex.Match(transcript, @"Files touched and all reverted: (.*?)\. Working tree").Groups[1].Value.Trim();
var constraints = $"- Task: \"{task}\"\n- Do not delete or weaken tests; do not change public signatures.\n- Working tree is clean at baseline (earlier edits reverted: {touched}).\n- Do NOT re-try any approach under Failed attempts. If you think one is right, first explain why it does not break the passing tests.";
var result = "- All visible tests pass (14/14); changes limited to library code, not tests.\n- Final reply: root cause in one paragraph, a diff summary, and the `dotnet test` summary line.";

var output = template
    .Replace("{{goal}}", task).Replace("{{state}}", state.ToString().TrimEnd())
    .Replace("{{files}}", files.ToString().TrimEnd()).Replace("{{attempts}}", attempts.ToString().TrimEnd())
    .Replace("{{rejected}}", rejected.ToString().TrimEnd()).Replace("{{question}}", question)
    .Replace("{{constraints}}", constraints).Replace("{{result}}", result);

// Drop the template preamble (title + instructions) so only the artifact remains.
output = "# Expert hand-off\n" + output[output.IndexOf("\n## Goal", StringComparison.Ordinal)..];
File.WriteAllText(outFile, output.TrimEnd() + "\n");
sw.Stop();
Console.WriteLine($"wrote {outFile}: {output.Length} chars, ~{output.Length / 4} tokens, {sw.Elapsed.TotalSeconds:F1}s");
return 0;
