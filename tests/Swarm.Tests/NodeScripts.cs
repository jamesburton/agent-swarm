using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>Runs generated workflow scripts under node (syntax checks and behavioural runs with stub workflow hooks).</summary>
public static class NodeScripts
{
    /// <summary>Environment variable that, when set to <c>1</c>, lets node-based tests pass vacuously on a machine without node.</summary>
    public const string AllowNoNodeVariable = "SWARM_ALLOW_NO_NODE";

    /// <summary>Returns whether node-based assertions should run; fails loudly when node is missing unless explicitly allowed.</summary>
    /// <returns>True when node is on PATH; false only when node is missing and <see cref="AllowNoNodeVariable"/> is <c>1</c>.</returns>
    public static bool Required() => Required(Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable(AllowNoNodeVariable));

    /// <summary>Testable core of <see cref="Required()"/>.</summary>
    /// <param name="pathVariable">The PATH value to search.</param>
    /// <param name="allowNoNode">The value of <see cref="AllowNoNodeVariable"/>.</param>
    /// <returns>True when node is found; false when it is missing and <paramref name="allowNoNode"/> is <c>1</c>.</returns>
    public static bool Required(string? pathVariable, string? allowNoNode)
    {
        if (FindNode(pathVariable) is not null) return true;
        Assert.True(allowNoNode == "1", $"node was not found on PATH, so the node-based script tests cannot run; install node or set {AllowNoNodeVariable}=1 to skip them explicitly");
        return false;
    }

    static string? FindNode(string? pathVariable) =>
        (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(p => new[] { Path.Combine(p, "node.exe"), Path.Combine(p, "node") })
        .FirstOrDefault(File.Exists);

    static (int Exit, string Out, string Err) RunNode(params string[] args)
    {
        var psi = new ProcessStartInfo(FindNode(Environment.GetEnvironmentVariable("PATH"))!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "node timed out");
        return (p.ExitCode, o.Result, e.Result);
    }

    static string TempFile(string ext, string content)
    {
        var f = Path.Combine(Path.GetTempPath(), $"swarm-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(f, content, new UTF8Encoding(false));
        return f;
    }

    /// <summary>Asserts that node accepts the script: the meta line as a module, the body as an async function body.</summary>
    /// <param name="js">The generated script.</param>
    public static void AssertNodeCheck(string js)
    {
        var nl = js.IndexOf('\n');
        foreach (var part in new[] { js[..nl] + "\n", "async function __body() {\n" + js[(nl + 1)..] + "\n}\n" })
        {
            var f = TempFile(".mjs", part);
            try
            {
                var (exit, _, err) = RunNode("--check", f);
                Assert.True(exit == 0, err);
            }
            finally { File.Delete(f); }
        }
    }

    // Runs a generated script under node with stub workflow hooks and prints { calls, logs, result } as JSON.
    // Modes: ok (every agent succeeds), blocked (workers block), allblocked (every fan-out agent blocks),
    // failed (every agent returns nothing), reject (reviewers reject).
    const string Harness = """
        import fs from 'node:fs';
        const [, , file, argsJson, mode] = process.argv;
        const src = fs.readFileSync(file, 'utf8').replace('export const meta =', 'const meta =');
        const calls = [], logs = [];
        const agent = async (prompt, opts) => {
          calls.push({ prompt, agentType: opts.agentType, opts });
          if (mode === 'failed') return null;
          if (opts.schema?.properties?.verdict)
            return mode === 'reject' ? { verdict: 'reject', notes: 'blocking: tests missing' } : { verdict: 'approve', notes: 'LGTM from ' + opts.agentType };
          if (mode === 'allblocked') return { status: 'blocked', branch: 'b', notes: 'still stuck' };
          if (mode === 'blocked' && opts.agentType === 'worker') return { status: 'blocked', branch: 'b', notes: 'stuck' };
          return { status: 'done', branch: 'br-' + calls.length, base: 'main', notes: '' };
        };
        const pipeline = async (items, ...stages) => Promise.all(items.map(async (it, i) => { let r = it; for (const s of stages) r = await s(r, it, i); return r; }));
        const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
        const result = await new AsyncFunction('agent', 'pipeline', 'parallel', 'phase', 'log', 'args', src)(agent, pipeline, async t => Promise.all(t.map(f => f())), () => {}, m => logs.push(String(m)), JSON.parse(argsJson));
        console.log(JSON.stringify({ calls, logs, result }));
        """;

    /// <summary>Runs a generated script under node with stub hooks.</summary>
    /// <param name="js">The generated script.</param>
    /// <param name="argsJson">The workflow <c>args</c> as JSON.</param>
    /// <param name="mode">The stub agent behaviour (see the harness).</param>
    /// <returns>Each agent call (type, prompt, options) and the script's result.</returns>
    public static (List<(string AgentType, string Prompt, JsonElement Opts)> Calls, JsonElement Result) RunScript(string js, string argsJson, string mode = "ok")
    {
        var h = TempFile(".mjs", Harness); var f = TempFile(".mjs", js);
        try
        {
            var (exit, o, e) = RunNode(h, f, argsJson, mode);
            Assert.True(exit == 0, e);
            var root = JsonDocument.Parse(o).RootElement;
            var calls = root.GetProperty("calls").EnumerateArray()
                .Select(c => (c.GetProperty("agentType").GetString()!, c.GetProperty("prompt").GetString()!, c.GetProperty("opts").Clone())).ToList();
            return (calls, root.GetProperty("result").Clone());
        }
        finally { File.Delete(h); File.Delete(f); }
    }
}
