// THROWAWAY SPIKE 4B: doc-lifecycle sweeper (deterministic) + optional haiku relevance check.
// Usage: dotnet run sweep.cs -- <docsDir> [--today 2026-10-02] [--json] [--llm]
//        dotnet run sweep.cs -- --selftest      (run from this folder; uses testdata/)
//
// Staleness thresholds (days since max(updated, reviewed); archive at 2x):
//   design decision 90 | device fact 180 | benchmark number 30 | default 90
// Doc type: front-matter `type:` (decision|device|benchmark) else filename heuristic, else default.
#:property PublishAot=false
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var argv = args.ToList();
if (argv.Contains("--selftest")) return SelfTest.Run();

string? GetOpt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
string? docsDir = argv.FirstOrDefault(a => !a.StartsWith("--") && a != GetOpt("--today"));
if (docsDir is null) { Console.Error.WriteLine("usage: sweep.cs -- <docsDir> [--today yyyy-MM-dd] [--json] [--llm]"); return 2; }
var today = GetOpt("--today") is { } t ? DateOnly.ParseExact(t, "yyyy-MM-dd") : DateOnly.FromDateTime(DateTime.Now);
bool json = argv.Contains("--json"), llm = argv.Contains("--llm");

var sw = Stopwatch.StartNew();
var reports = Sweeper.Sweep(Path.GetFullPath(docsDir), today);
long detMs = sw.ElapsedMilliseconds;
int calls = 0;
if (llm) calls = Llm.Check(reports, Path.GetFullPath(Path.Combine(docsDir, "..")), 8);
sw.Stop();

int errors = reports.Sum(r => r.Errors.Count);
if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(new { today = today.ToString("yyyy-MM-dd"), llmCalls = calls, deterministicMs = detMs, totalMs = sw.ElapsedMilliseconds, errors, docs = reports },
        new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    foreach (var r in reports)
    {
        Console.WriteLine($"{r.File}  age={(r.AgeDays?.ToString() ?? "?")}d type={r.Type} threshold={r.ThresholdDays}d status={r.Status}" +
            (r.Verdict is null ? "" : $" llm={r.Verdict} ({r.Reason})"));
        foreach (var e in r.Errors) Console.WriteLine($"    ERROR {e}");
        foreach (var w in r.Warnings) Console.WriteLine($"    warn  {w}");
    }
    Console.WriteLine($"-- {reports.Count} docs, {errors} errors, {reports.Sum(r => r.Warnings.Count)} warnings, deterministic {detMs} ms, total {sw.ElapsedMilliseconds} ms" + (llm ? $", {calls} LLM calls made" : ""));
}
return errors > 0 ? 1 : 0;

record DocReport(string File, string Type, int ThresholdDays, int? AgeDays, string Status)
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Verdict { get; set; }
    public string? Reason { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Body { get; set; } = "";
}

static class Sweeper
{
    static readonly Regex Link = new(@"\]\(([^)\s#]+)(#[^)]*)?\)", RegexOptions.Compiled);

    public static (Dictionary<string, string> fm, string body) FrontMatter(string text)
    {
        var fm = new Dictionary<string, string>();
        var m = Regex.Match(text, @"\A---\r?\n(.*?)\r?\n---\r?\n?", RegexOptions.Singleline);
        if (!m.Success) return (fm, text);
        foreach (var line in m.Groups[1].Value.Split('\n'))
        {
            var kv = Regex.Match(line.TrimEnd('\r'), @"^([A-Za-z_]+):\s*([^#]*?)\s*(#.*)?$");
            if (kv.Success) fm[kv.Groups[1].Value] = kv.Groups[2].Value;
        }
        return (fm, text[m.Length..]);
    }

    public static (string type, int days) TypeOf(string file, Dictionary<string, string> fm)
    {
        string key = fm.GetValueOrDefault("type", "").ToLowerInvariant();
        string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (key == "")
            key = Regex.IsMatch(n, "benchmark|results|perf|measure") ? "benchmark"
                : Regex.IsMatch(n, "device|hardware|capabilit|probe") ? "device"
                : Regex.IsMatch(n, "decision|spec|design") ? "decision" : "default";
        return key switch { "benchmark" => (key, 30), "device" => (key, 180), "decision" => (key, 90), _ => ("default", 90) };
    }

    public static string Status(int? age, int threshold) =>
        age is null ? "unknown" : age >= 2 * threshold ? "archive" : age >= threshold ? "stale?" : "current";

    static DateOnly? ParseDate(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static IEnumerable<string> RelLinks(string body) =>
        Link.Matches(body).Select(m => m.Groups[1].Value).Where(l => !Regex.IsMatch(l, @"^([a-z][a-z0-9+.-]*:|/|#)", RegexOptions.IgnoreCase));

    static HashSet<string> LinkedFrom(string file)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return set;
        var dir = Path.GetDirectoryName(file)!;
        foreach (var l in RelLinks(File.ReadAllText(file)))
            try { set.Add(Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(l)))); } catch { }
        return set;
    }

    public static List<DocReport> Sweep(string docsDir, DateOnly today)
    {
        var root = Path.GetFullPath(Path.Combine(docsDir, ".."));
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "AGENTS.md", "README.md" }) indexed.UnionWith(LinkedFrom(Path.Combine(root, f)));

        var list = new List<DocReport>();
        foreach (var path in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var (fm, body) = FrontMatter(File.ReadAllText(path));
            var (type, th) = TypeOf(path, fm);
            DateOnly? created = null, updated = null;
            var errs = new List<string>();
            foreach (var k in new[] { "created", "updated" })
            {
                if (!fm.TryGetValue(k, out var v) || v == "") { errs.Add($"missing `{k}`"); continue; }
                var d = ParseDate(v);
                if (d is null) errs.Add($"invalid `{k}`: '{v}'"); else if (k == "created") created = d; else updated = d;
            }
            if (created is { } c && updated is { } u && u < c) errs.Add("`updated` earlier than `created`");
            if (updated is { } up && up > today) errs.Add("`updated` is in the future");
            var reviewed = ParseDate(fm.GetValueOrDefault("reviewed"));
            var basis = new[] { updated, reviewed }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
            int? age = updated is null ? null : today.DayNumber - basis.DayNumber;
            var r = new DocReport(Path.GetRelativePath(root, path).Replace('\\', '/'), type, th, age, Status(age, th)) { Body = body };
            r.Errors.AddRange(errs);

            if (fm.TryGetValue("status", out var st) && st == "archived") r.Warnings.Add("front-matter status is archived but file still present");
            if (!indexed.Contains(Path.GetFullPath(path))) r.Warnings.Add("index drift: not linked from AGENTS.md or README.md");

            var dir = Path.GetDirectoryName(path)!;
            foreach (var l in RelLinks(body).Distinct())
            {
                string target;
                try { target = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(l))); } catch { r.Errors.Add($"bad link '{l}'"); continue; }
                if (!File.Exists(target) && !Directory.Exists(target)) r.Errors.Add($"broken link '{l}'");
            }
            list.Add(r);
        }
        return list;
    }
}

static class Llm
{
    const string Schema = "{\"verdict\":\"keep|refine|archive\",\"reason\":\"<=20 words\"}";

    static string Run(string argsLine, string stdin, string cwd, int timeoutMs, out bool ok)
    {
        ok = false;
        try
        {
            var psi = new ProcessStartInfo("claude", argsLine) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            p.StandardInput.Write(stdin); p.StandardInput.Close();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return "timeout"; }
            ok = p.ExitCode == 0;
            return ok ? so.Result : $"exit {p.ExitCode}: {se.Result.Trim()}";
        }
        catch (Exception e) { return e.Message; }
    }

    static string Git(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, UseShellExecute = false };
            using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return o.Trim();
        }
        catch { return ""; }
    }

    public static int Check(List<DocReport> reports, string root, int cap)
    {
        int calls = 0;
        var tracked = Git(root, "ls-files").Split('\n').Take(60);
        foreach (var r in reports.Where(r => r.Status is "stale?" or "archive"))
        {
            if (calls >= cap) { r.Verdict = "skipped"; r.Reason = $"call cap {cap} reached"; continue; }
            // repo paths mentioned in the doc (backticks or links) that exist
            var paths = Regex.Matches(r.Body, @"`([\w./\\-]+\.[\w]+|[\w.-]+/[\w./-]*)`").Select(m => m.Groups[1].Value.TrimEnd('/'))
                .Concat(Sweeper.RelLinks(r.Body)).Select(p => p.Replace('\\', '/')).Distinct()
                .Where(p => !p.Contains("..") && (File.Exists(Path.Combine(root, p)) || Directory.Exists(Path.Combine(root, p)))).Take(10).ToList();
            var log = paths.Count > 0 ? Git(root, "log -5 --format=%cs%x20%s -- " + string.Join(' ', paths.Select(p => $"\"{p}\""))) : "(no repo paths mentioned)";
            var prompt = new StringBuilder()
                .AppendLine("You review one documentation file for staleness against its repo. Reply with ONLY strict JSON, no prose, no code fence: " + Schema)
                .AppendLine($"Meaning: keep = still accurate; refine = partly outdated; archive = obsolete. Doc age: {r.AgeDays} days (type {r.Type}, proposed {r.Status}).")
                .AppendLine("Repo files (first 60):").AppendLine(string.Join("\n", tracked))
                .AppendLine("Paths mentioned by the doc that exist: " + (paths.Count == 0 ? "(none)" : string.Join(", ", paths)))
                .AppendLine("git log -5 for those paths:").AppendLine(log)
                .AppendLine($"=== DOC {r.File} ===").AppendLine(r.Body.Length > 12000 ? r.Body[..12000] : r.Body).ToString();
            calls++;
            var raw = Run("-p --model haiku", prompt, root, 120_000, out bool ok);
            if (!ok) { r.Verdict = "unavailable"; r.Reason = raw.Length > 120 ? raw[..120] : raw; continue; }
            var m = Regex.Match(raw, @"\{[^{}]*\}", RegexOptions.Singleline);
            try
            {
                using var d = JsonDocument.Parse(m.Value);
                var v = d.RootElement.GetProperty("verdict").GetString();
                if (v is not ("keep" or "refine" or "archive")) throw new FormatException(v);
                r.Verdict = v; r.Reason = d.RootElement.GetProperty("reason").GetString();
            }
            catch { r.Verdict = "unavailable"; r.Reason = "unparseable: " + (raw.Length > 100 ? raw[..100] : raw).Replace('\n', ' '); }
        }
        return calls;
    }
}

static class SelfTest
{
    static int fails;
    static void Eq(object? exp, object? act, string n) { if (!Equals(exp, act)) { fails++; Console.WriteLine($"FAIL {n}: expected {exp} got {act}"); } else Console.WriteLine($"ok   {n}"); }

    public static int Run()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "testdata", "docs");
        if (!Directory.Exists(dir)) { Console.WriteLine("run from spikes/04-doc-sweeper/b; testdata/docs missing"); return 2; }
        var rs = Sweeper.Sweep(dir, new DateOnly(2026, 10, 2)).ToDictionary(r => Path.GetFileName(r.File));
        Eq("current", rs["fresh.md"].Status, "fresh current");
        Eq(0, rs["fresh.md"].Errors.Count, "fresh no errors");
        Eq("stale?", rs["old-decision.md"].Status, "decision 100d stale?");
        Eq("archive", rs["benchmark-old.md"].Status, "benchmark 65d archive");
        Eq("device", rs["device-fact.md"].Type, "device type via front-matter");
        Eq("current", rs["device-fact.md"].Status, "device 150d current");
        Eq(true, rs["nodates.md"].Errors.Contains("missing `created`") && rs["nodates.md"].Errors.Contains("missing `updated`"), "missing dates");
        Eq(true, rs["baddate.md"].Errors.Any(e => e.StartsWith("invalid `updated`")), "invalid date");
        Eq(true, rs["broken.md"].Errors.Any(e => e.Contains("broken link 'nope.md'")), "broken link");
        Eq(false, rs["broken.md"].Errors.Any(e => e.Contains("fresh.md") || e.Contains("example.com")), "good/external links ok");
        Eq(false, rs["fresh.md"].Warnings.Any(w => w.Contains("index drift")), "fresh is indexed");
        Eq(true, rs["orphan.md"].Warnings.Any(w => w.Contains("index drift")), "orphan drift");
        Eq(true, Sweeper.Status(180, 90) == "archive" && Sweeper.Status(179, 90) == "stale?" && Sweeper.Status(90, 90) == "stale?" && Sweeper.Status(89, 90) == "current", "boundaries");
        Console.WriteLine(fails == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({fails})");
        return fails == 0 ? 0 : 1;
    }
}
