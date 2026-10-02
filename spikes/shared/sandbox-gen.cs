// THROWAWAY SPIKE: synthetic .NET sandbox generator.
// Usage: dotnet run sandbox-gen.cs -- <outDir> [--projects 12] [--tests-per 20] [--delay-ms 150] [--seed 1] [--failing 0] [--tasks 16]
//        [--conflicts K] [--overlap K] [--stacked K]   (K task PAIRS each; default 0 = old behaviour)
// --conflicts K: K pairs edit the SAME lines of the same file (genuine textual conflict when both merge).
// --overlap K:   K pairs edit DIFFERENT regions of the same file (clean merge, shared `touches`).
// --stacked K:   K pairs where task b is branched from task a and re-edits a's line (merges clean only while a is
//                unlanded; after a squash-lands, merging b conflicts, but `git rebase` drops a's patch and is clean).
// Pair tasks take the LAST 2*(K total) ids, adjacent (conflicts first, then overlap, then stacked).
using System.Diagnostics;
using System.Text;

if (args.Length == 0 || args[0].StartsWith("--"))
{
    Console.Error.WriteLine("usage: sandbox-gen.cs -- <outDir> [--projects N] [--tests-per N] [--delay-ms N] [--seed N] [--failing K] [--tasks N] [--conflicts K] [--overlap K] [--stacked K]");
    return 2;
}

string outDir = Path.GetFullPath(args[0]);
int Opt(string name, int def)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : def;
}

int n = Opt("projects", 12), testsPer = Opt("tests-per", 20), delay = Opt("delay-ms", 150);
int seed = Opt("seed", 1), failing = Opt("failing", 0), taskCount = Opt("tasks", 16);
int nConf = Opt("conflicts", 0), nOver = Opt("overlap", 0), nStack = Opt("stacked", 0);
int nPairs = nConf + nOver + nStack;
const int FilesPerLib = 4; // Foo.cs, Foo1.cs, Foo2.cs, Foo3.cs: one per task so merges never conflict textually
if (taskCount - nPairs > n * FilesPerLib) throw new ArgumentException("too many tasks for projects");
if ((failing + nPairs) * 2 > taskCount) throw new ArgumentException("--failing/--conflicts/--overlap/--stacked need 2 tasks per pair");

if (Directory.Exists(outDir))
{
    // .git object files are read-only on Windows; clear the attribute or recursive delete throws.
    foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
    Directory.Delete(outDir, true);
}
Directory.CreateDirectory(outDir);

void Write(string rel, string content)
{
    string p = Path.Combine(outDir, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    File.WriteAllText(p, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
}

string Lib(int k) => $"Lib{k:00}";
string ClassName(int j) => j == 0 ? "Foo" : $"Foo{j}";
int BaseCode(int k, int j) => 1000 + k * 10 + j;
string FooSource(int k, int j, string note, int code, string computeExtra = "") =>
    $"namespace {Lib(k)};\n\npublic static class {ClassName(j)}\n{{\n    public const int Code = {code};\n\n    public const string Note = \"{note}\";\n\n    public static int Compute(int x) => x + {k}{computeExtra};\n}}\n";

Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>\n");
Write(".gitignore", "bin/\nobj/\n");

var slnx = new StringBuilder("<Solution>\n");
for (int k = 1; k <= n; k++)
{
    string lib = Lib(k), tests = lib + ".Tests";
    var libProj = new StringBuilder("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
    if (k > 1) libProj.Append($"  <ItemGroup>\n    <ProjectReference Include=\"..\\{Lib(k - 1)}\\{Lib(k - 1)}.csproj\" />\n  </ItemGroup>\n");
    libProj.Append("</Project>\n");
    Write($"{lib}/{lib}.csproj", libProj.ToString());
    for (int j = 0; j < FilesPerLib; j++) Write($"{lib}/{ClassName(j)}.cs", FooSource(k, j, "base", BaseCode(k, j)));

    Write($"{tests}/{tests}.csproj",
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" +
        "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />\n" +
        "    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
        "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n" +
        "  </ItemGroup>\n  <ItemGroup>\n" +
        $"    <ProjectReference Include=\"..\\{lib}\\{lib}.csproj\" />\n  </ItemGroup>\n</Project>\n");

    var t = new StringBuilder($"using Xunit;\n\nnamespace {tests};\n\npublic class SlowTests\n{{\n");
    for (int i = 1; i <= testsPer; i++)
        t.Append($"    [Fact]\n    public void Test{i:000}()\n    {{\n        Thread.Sleep({delay});\n        Assert.Equal({k + 1}, {lib}.Foo.Compute(1));\n    }}\n\n");
    t.Length -= 1;
    t.Append("}\n");
    Write($"{tests}/SlowTests.cs", t.ToString());
    slnx.Append($"  <Project Path=\"{lib}/{lib}.csproj\" />\n  <Project Path=\"{tests}/{tests}.csproj\" />\n");
}

// Task plan: task i touches file (lib, j); the lib order is shuffled by the seed.
var rng = new Random(seed);
// Each pair shares ONE file slot, so only taskCount - nPairs slots are consumed; singles come first, pairs last.
var slots = Enumerable.Range(1, n).SelectMany(k => Enumerable.Range(0, FilesPerLib).Select(j => (k, j)))
    .OrderBy(_ => rng.Next()).Take(taskCount - nPairs).ToList();
int nSingles = taskCount - 2 * nPairs;
var tasks = new List<(string id, int k, int j)>();
var kindOf = new Dictionary<string, string>(); // task id -> plain | conflict-a/b | overlap-a/b | stack-a/b
for (int i = 0; i < nSingles; i++) { tasks.Add(($"T{i + 1:000}", slots[i].k, slots[i].j)); kindOf[tasks[^1].id] = "plain"; }
string[] pairKinds = [.. Enumerable.Repeat("conflict", nConf), .. Enumerable.Repeat("overlap", nOver), .. Enumerable.Repeat("stack", nStack)];
for (int p = 0; p < nPairs; p++)
{
    var (pk, pj) = slots[nSingles + p];
    string a = $"T{nSingles + 2 * p + 1:000}", b = $"T{nSingles + 2 * p + 2:000}";
    tasks.Add((a, pk, pj)); tasks.Add((b, pk, pj));
    kindOf[a] = pairKinds[p] + "-a"; kindOf[b] = pairKinds[p] + "-b";
}

// Seeded interactions: pair (2p, 2p+1) in different libs both set Code to the same value (9000+p+1).
// Each alone keeps all codes unique; together they collide. The assertion lives in the top test project.
var pairs = new List<(string a, string b, int value)>();
var pool = tasks.Where(t => kindOf[t.id] == "plain").ToList();
for (int p = 0; p < failing; p++)
{
    var a = pool[0];
    var b = pool.Skip(1).FirstOrDefault(x => x.k != a.k);
    if (b == default) throw new InvalidOperationException("could not pick pair in different libs");
    pool.Remove(a); pool.Remove(b);
    pairs.Add((a.id, b.id, 9001 + p));
}
var edits = new Dictionary<string, int>(); // task id -> overriding Code
foreach (var (a, b, v) in pairs) { edits[a] = v; edits[b] = v; }

string TaskPath((string id, int k, int j) t) => $"{Lib(t.k)}/{ClassName(t.j)}.cs";
string CodeOf(string id) { var t = tasks.First(x => x.id == id); return $"{Lib(t.k)}.{ClassName(t.j)}.Code"; }

if (pairs.Count > 0)
{
    var s = new StringBuilder($"using Xunit;\n\nnamespace {Lib(n)}.Tests;\n\npublic class InteractionTests\n{{\n");
    for (int p = 0; p < pairs.Count; p++)
        s.Append($"    [Fact]\n    public void Interaction{p + 1:00}_{pairs[p].a}_{pairs[p].b}_CodesMustBeDistinct()\n    {{\n        Thread.Sleep({delay});\n        Assert.NotEqual({CodeOf(pairs[p].a)}, {CodeOf(pairs[p].b)});\n    }}\n\n");
    s.Length -= 1;
    s.Append("}\n");
    Write($"{Lib(n)}.Tests/InteractionTests.cs", s.ToString());
}

Write("tasks.json", "[\n" + string.Join(",\n", tasks.Select(t =>
    $"  {{ \"id\": \"{t.id}\", \"touches\": [\"{TaskPath(t)}\"], \"branch\": \"task/{t.id}\" }}")) + "\n]\n");
Write("Sandbox.slnx", slnx.Append("</Solution>\n").ToString());

var readme = new StringBuilder("# Synthetic sandbox (generated, throwaway)\n\n");
readme.Append($"Params: projects={n}, tests-per={testsPer}, delay-ms={delay}, seed={seed}, failing={failing}, tasks={taskCount}.\n");
readme.Append($"Serial suite cost is about {n * testsPer * delay / 1000.0:0.#}s of sleep; run with `dotnet test Sandbox.slnx`.\n\n");
readme.Append("Each task branch (`task/Txxx`, branched from `main`) edits one file listed in `tasks.json`; branches merge cleanly in any combination (except the same-file pairs listed below).\n\n");
if (nPairs > 0)
{
    readme.Append($"## Same-file pairs (conflicts={nConf}, overlap={nOver}, stacked={nStack})\n\n| Pair | Kind | File | Edit |\n|---|---|---|---|\n");
    foreach (var a in tasks.Where(t => kindOf[t.id].EndsWith("-a")))
    {
        var b = tasks[tasks.IndexOf(a) + 1];
        string kind = kindOf[a.id][..^2];
        string edit = kind switch { "conflict" => "both rewrite the `Note` line (textual conflict)", "overlap" => "a edits `Code`, b edits `Compute` (clean merge, shared touches)", _ => "b branched from a, rewrites `Note` again (conflicts after a squash-lands; rebase is clean)" };
        readme.Append($"| {a.id} + {b.id} | {kind} | `{TaskPath(a)}` | {edit} |\n");
    }
    readme.Append('\n');
}
readme.Append("## Seeded interactions\n\n");
if (pairs.Count == 0) readme.Append("None (`--failing 0`): every combination of branches passes.\n");
else
{
    readme.Append("| Test | Tasks | Behaviour |\n|---|---|---|\n");
    for (int p = 0; p < pairs.Count; p++)
        readme.Append($"| `Interaction{p + 1:00}_*` in `{Lib(n)}.Tests` | {pairs[p].a} + {pairs[p].b} | Each sets the `Code` constant in its own file to {pairs[p].value} (a duplicate only when both are merged); the test fails only when both branches are merged. Each alone passes. |\n");
}
Write("README.md", readme.ToString());

// Git: init, commit base on main, then one branch per task with a trivial valid edit.
void Git(params string[] a)
{
    var psi = new ProcessStartInfo("git") { WorkingDirectory = outDir, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var x in new[] { "-c", "user.name=sandbox", "-c", "user.email=sandbox@example.invalid", "-c", "core.autocrlf=false" }.Concat(a)) psi.ArgumentList.Add(x);
    using var pr = Process.Start(psi)!;
    string err = pr.StandardError.ReadToEnd(); pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
    if (pr.ExitCode != 0) throw new Exception($"git {string.Join(' ', a)} failed: {err}");
}

Git("init", "-q", "-b", "main");
Git("add", "-A");
Git("commit", "-q", "-m", "Base sandbox");
foreach (var t in tasks)
{
    string kind = kindOf[t.id];
    // stack-b branches from its partner (task a), so its history contains a's commit.
    Git("checkout", "-q", "-b", $"task/{t.id}", kind == "stack-b" ? $"task/{tasks[tasks.IndexOf(t) - 1].id}" : "main");
    int code = edits.TryGetValue(t.id, out var v) ? v : BaseCode(t.k, t.j);
    string extra = "", note = t.id;
    if (kind == "overlap-a") { code = 7000 + t.k * 10 + t.j; note = "base"; }
    if (kind == "overlap-b") { extra = " + 0"; note = "base"; }
    Write(TaskPath(t), FooSource(t.k, t.j, note, code, extra));
    Git("commit", "-q", "-am", $"{t.id}: edit {TaskPath(t)}");
    Git("checkout", "-q", "main");
}

Console.WriteLine($"Generated {outDir}: {n} libs, {n} test projects, {tasks.Count} task branches, {pairs.Count} seeded interactions, {nConf} conflict / {nOver} overlap / {nStack} stacked pairs.");
foreach (var (a, b, v) in pairs) Console.WriteLine($"  interacting pair: {a} + {b}");
return 0;
