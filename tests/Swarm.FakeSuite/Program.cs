// Fake test suite for swarm tool tests: verdict from *.fail rule files in the working directory, plus
// options to sleep, spawn a waited-for child, or leave an orphan child that holds the output pipes.
using System.Diagnostics;
using System.Globalization;

var options = ParseOptions(args);
Console.WriteLine("fake-suite args: " + string.Join(' ', args));

if (options.TryGetValue("--orphan-ms", out var orphanMs))
{
    var orphan = Spawn(orphanMs);
    WritePid(options, orphan.Id);
    Console.WriteLine("fake-suite: green");
    return 0;
}

if (options.TryGetValue("--child-sleep-ms", out var childMs))
{
    using var child = Spawn(childMs);
    WritePid(options, child.Id);
    child.WaitForExit();
}

if (options.TryGetValue("--sleep-ms", out var sleepMs))
{
    Thread.Sleep(int.Parse(sleepMs, CultureInfo.InvariantCulture));
}

var cwd = Directory.GetCurrentDirectory();
foreach (var rule in Directory.EnumerateFiles(cwd, "*.fail").Order(StringComparer.Ordinal))
{
    var required = File.ReadAllLines(rule).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    if (required.All(r => File.Exists(Path.Combine(cwd, r))))
    {
        Console.WriteLine($"fake-suite: red ({Path.GetFileName(rule)})");
        return 1;
    }
}

Console.WriteLine("fake-suite: green");
return 0;

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal) && !a[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            d[a[i]] = a[++i];
        }
    }

    return d;
}

// The child inherits this process's std handles (no redirection), so an orphan keeps the caller's pipes open.
static Process Spawn(string sleepMs)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    psi.ArgumentList.Add("--sleep-ms");
    psi.ArgumentList.Add(sleepMs);
    return Process.Start(psi)!;
}

static void WritePid(Dictionary<string, string> o, int pid)
{
    if (o.TryGetValue("--pid-file", out var f))
    {
        File.WriteAllText(f, pid.ToString(CultureInfo.InvariantCulture));
    }
}
