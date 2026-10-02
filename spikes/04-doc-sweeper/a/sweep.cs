// THROWAWAY SPIKE 4A: deterministic doc-lifecycle sweeper (see docs/doc-lifecycle.md).
// Usage: dotnet run sweep.cs -- <docsDir> [--today 2026-10-02] [--json]
//        dotnet run sweep.cs -- --selftest
// Exit code: 1 if any error finding (missing/invalid timestamps, broken links), else 0.
//
// ---- Threshold table (days since max(updated, reviewed)); "stale?" at >= T, "archive" at >= 2T ----
// Type comes from front-matter `type:` (design|decision, device, benchmark) else path/name heuristics.
#:property PublishAot=false
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

var thresholds = new Dictionary<string, int> { ["design-decision"] = 90, ["device-fact"] = 180, ["benchmark"] = 30, ["default"] = 90 };

// Heuristic keyword -> type, matched against the relative path (lowercase). First match wins.
var heuristics = new (string Type, string[] Words)[]
{
    ("benchmark", new[] { "benchmark", "perf", "results" }),
    ("device-fact", new[] { "device", "hardware", "pinout", "datasheet" }),
    ("design-decision", new[] { "decision", "design", "spec", "plan", "adr" }),
};

var sw = Stopwatch.StartNew();
if (args.Contains("--selftest")) return SelfTest();

int ti = Array.IndexOf(args, "--today");
string? docsArg = args.Where((a, i) => !a.StartsWith("--") && (ti < 0 || i != ti + 1)).FirstOrDefault();
if (docsArg is null) { Console.Error.WriteLine("usage: sweep.cs -- <docsDir> [--today yyyy-MM-dd] [--json]"); return 2; }
var today = ti >= 0 ? DateOnly.ParseExact(args[ti + 1], "yyyy-MM-dd") : DateOnly.FromDateTime(DateTime.Now);
var findings = Sweep(Path.GetFullPath(docsArg), today);
sw.Stop();

bool anyError = findings.Any(f => f.Severity == "error");
if (args.Contains("--json"))
{
    var counts = findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine(JsonSerializer.Serialize(new { today = today.ToString("yyyy-MM-dd"), runtimeMs = sw.ElapsedMilliseconds, counts, findings }, new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    foreach (var f in findings) Console.WriteLine($"{f.Severity,-7} {f.Kind,-16} {f.File}  {f.Detail}");
    Console.WriteLine($"-- {findings.Count} findings, {findings.Count(f => f.Severity == "error")} errors, {sw.ElapsedMilliseconds} ms");
}
return anyError ? 1 : 0;

// ---------------------------------------------------------------------------

List<Finding> Sweep(string docsDir, DateOnly today)
{
    var res = new List<Finding>();
    var root = Path.GetDirectoryName(docsDir.TrimEnd('\\', '/'))!;
    var files = Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();

    // Index: every link target in the parent's AGENTS.md / README.md.
    var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var idx in new[] { "AGENTS.md", "README.md" })
    {
        var p = Path.Combine(root, idx);
        if (!File.Exists(p)) continue;
        foreach (var t in ExtractLinks(File.ReadAllText(p)))
            if (Resolve(Path.GetDirectoryName(p)!, t) is { } full) indexed.Add(Path.GetFullPath(full));
    }

    foreach (var file in files)
    {
        var rel = Path.GetRelativePath(docsDir, file).Replace('\\', '/');
        var text = File.ReadAllText(file);
        var fm = ParseFrontMatter(text);
        void Add(string sev, string kind, string detail) => res.Add(new Finding(rel, sev, kind, detail));

        if (fm is null) Add("info", "no-front-matter", "file has no front-matter block");
        DateOnly? created = CheckDate(fm, "created", Add), updated = CheckDate(fm, "updated", Add);
        DateOnly? reviewed = CheckDate(fm, "reviewed", Add, optional: true);
        if (fm is not null && fm.TryGetValue("status", out var st) && st is not ("current" or "stale?" or "archived"))
            Add("warning", "invalid-status", $"status '{st}' not in current|stale?|archived");

        var type = InferType(fm, rel);
        var basis = new[] { updated, reviewed }.Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Max();
        if (updated.HasValue)
        {
            int age = today.DayNumber - basis.DayNumber, t = thresholds[type];
            string proposed = age >= 2 * t ? "archive" : age >= t ? "stale?" : "current";
            Add("info", "age", $"{age}d type={type} threshold={t}d proposed={proposed}");
            if (proposed != "current") Add("warning", proposed, $"{age}d old, type={type}, threshold {t}d");
            if (basis > today) Add("warning", "future-date", $"{basis:yyyy-MM-dd} is after today");
        }

        if (!indexed.Contains(Path.GetFullPath(file))) Add("warning", "index-drift", "not linked from ../AGENTS.md or ../README.md");

        foreach (var target in ExtractLinks(text))
        {
            var full = Resolve(Path.GetDirectoryName(file)!, target);
            if (full is null) continue;
            if (!File.Exists(full) && !Directory.Exists(full)) Add("error", "broken-link", target);
        }
    }
    return res;
}

DateOnly? CheckDate(Dictionary<string, string>? fm, string key, Action<string, string, string> add, bool optional = false)
{
    if (fm is null || !fm.TryGetValue(key, out var v) || v.Length == 0)
    {
        if (!optional) add("error", $"missing-{key}", "required timestamp absent");
        return null;
    }
    if (DateOnly.TryParseExact(v, "yyyy-MM-dd", out var d)) return d;
    add(optional ? "warning" : "error", $"invalid-{key}", $"'{v}' is not an ISO yyyy-MM-dd date");
    return null;
}

string InferType(Dictionary<string, string>? fm, string rel)
{
    if (fm is not null && fm.TryGetValue("type", out var t))
    {
        t = t.ToLowerInvariant();
        if (t.StartsWith("decision") || t.StartsWith("design")) return "design-decision";
        if (t.StartsWith("device")) return "device-fact";
        if (t.StartsWith("bench")) return "benchmark";
    }
    var low = rel.ToLowerInvariant();
    foreach (var (type, words) in heuristics) if (words.Any(low.Contains)) return type;
    return "default";
}

Dictionary<string, string>? ParseFrontMatter(string text)
{
    var lines = text.Replace("\r\n", "\n").Split('\n');
    if (lines.Length == 0 || lines[0].Trim() != "---") return null;
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < lines.Length; i++)
    {
        if (lines[i].Trim() == "---") return d;
        var m = Regex.Match(lines[i], @"^([A-Za-z_][\w-]*)\s*:\s*(.*?)\s*(?:\s#.*)?$");
        if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"', '\'');
    }
    return null; // unterminated block
}

// Relative link targets outside fenced blocks and inline code. External/anchor-only links are dropped.
IEnumerable<string> ExtractLinks(string text)
{
    bool fenced = false;
    foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
    {
        if (raw.TrimStart().StartsWith("```") || raw.TrimStart().StartsWith("~~~")) { fenced = !fenced; continue; }
        if (fenced) continue;
        var line = Regex.Replace(raw, "`[^`]*`", "");
        foreach (Match m in Regex.Matches(line, @"\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)"))
        {
            var t = m.Groups[1].Value;
            if (t.StartsWith("#") || Regex.IsMatch(t, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) continue;
            yield return t;
        }
    }
}

string? Resolve(string baseDir, string target)
{
    var path = Uri.UnescapeDataString(target.Split('#')[0].Split('?')[0]);
    return path.Length == 0 ? null : Path.GetFullPath(Path.Combine(baseDir, path));
}

int SelfTest()
{
    var cands = new[] { Path.Combine(Directory.GetCurrentDirectory(), "testdata", "docs"), Path.Combine(Directory.GetCurrentDirectory(), "spikes", "04-doc-sweeper", "a", "testdata", "docs") };
    var dir = cands.FirstOrDefault(Directory.Exists);
    if (dir is null) { Console.Error.WriteLine("selftest: testdata/docs not found"); return 2; }
    var f = Sweep(dir, new DateOnly(2026, 10, 2));
    int fail = 0;
    void Expect(string file, string kind, bool present = true)
    {
        bool has = f.Any(x => x.File == file && x.Kind == kind);
        if (has != present) { fail++; Console.WriteLine($"FAIL {file} {(present ? "expected" : "unexpected")} {kind}"); } else Console.WriteLine($"ok   {file} {(present ? "has" : "lacks")} {kind}");
    }
    Expect("valid.md", "missing-created", false); Expect("valid.md", "stale?", false); Expect("valid.md", "index-drift", false); Expect("valid.md", "broken-link", false);
    Expect("missing-date.md", "missing-updated"); Expect("missing-date.md", "missing-created", false);
    Expect("bad-date.md", "invalid-created"); Expect("bad-date.md", "invalid-updated");
    Expect("stale-age.md", "stale?"); Expect("stale-age.md", "archive", false);
    Expect("old-benchmark.md", "archive");
    Expect("broken-link.md", "broken-link"); Expect("broken-link.md", "index-drift", false);
    Expect("orphan.md", "index-drift");
    int brokenCount = f.Count(x => x.Kind == "broken-link");
    if (brokenCount != 1) { fail++; Console.WriteLine($"FAIL expected exactly 1 broken-link, got {brokenCount} (code/fenced links must be ignored)"); }
    Console.WriteLine(fail == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({fail})");
    return fail == 0 ? 0 : 1;
}

record Finding(string File, string Severity, string Kind, string Detail);
