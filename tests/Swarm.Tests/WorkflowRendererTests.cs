using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Swarm.Core;
using Swarm.Render;

public class WorkflowRendererTests
{
    static SwarmDefinition Sample() => TestSamples.Parsed();

    static IReadOnlyDictionary<string, string> Files(SwarmDefinition? s = null) => WorkflowRenderer.Render(s ?? Sample());

    static string Js(int n = 1, SwarmDefinition? s = null) => Files(s)[$".claude/workflows/{(s ?? Sample()).Name}.{n}.js"];

    static string Steps(SwarmDefinition? s = null) => Files(s)[$".claude/workflows/{(s ?? Sample()).Name}.steps.md"];

    static SwarmDefinition WithFlow(params Stage[] flow) => Sample() with { Flow = flow };

    static string Msg(Action a)
    {
        var m = Assert.Throws<SwarmException>(a).Message;
        Assert.DoesNotContain('\n', m);
        return m;
    }

    static IEnumerable<string> AllScripts(SwarmDefinition? s = null) => Files(s).Where(p => p.Key.EndsWith(".js")).Select(p => p.Value);

    [Fact] public void SampleProducesTwoSegmentsAndRunbook() =>
        Assert.Equal(new[] { ".claude/workflows/epic-delivery.1.js", ".claude/workflows/epic-delivery.2.js", ".claude/workflows/epic-delivery.steps.md" }, Files().Keys);

    [Fact] public void ScriptsHaveNoShellCommands()
    {
        foreach (var js in AllScripts()) { Assert.DoesNotContain("dnx", js); Assert.DoesNotContain("--yes", js); }
    }

    [Fact] public void ScriptsAreLfAsciiOnly()
    {
        foreach (var js in AllScripts()) Assert.All(js, c => Assert.True(c == '\n' || (c >= ' ' && c < 127), $"bad char U+{(int)c:X4}"));
    }

    [Fact] public void MetaIsFirstAndPureLiteral()
    {
        for (var n = 1; n <= 2; n++)
        {
            var first = Js(n).Split('\n')[0];
            Assert.StartsWith("export const meta = {", first);
            Assert.EndsWith("}", first);
            Assert.DoesNotContain("`", first);
            Assert.DoesNotContain("${", first);
            Assert.DoesNotContain("(", first);
        }

        Assert.Contains("name: \"epic-delivery-1\"", Js(1));
        Assert.Contains("phases: [{ title: \"worker\" }]", Js(1));
        Assert.Contains("phases: [{ title: \"gate batch-green\" }, { title: \"reviewer\" }]", Js(2));
    }

    [Fact] public void Segment1FiltersDoneReturnsUnresolvedAndReadsTasks()
    {
        var js = Js(1);
        Assert.Contains("status === 'done'", js);
        Assert.Contains("unresolved", js);
        Assert.Contains("args.tasks", js);
        Assert.DoesNotContain("args?.gates", js);
        Assert.DoesNotContain("args.state", js);
    }

    [Fact] public void Segment1EscalatesBlockedToExpert()
    {
        var js = Js(1);
        Assert.Contains("agentType: \"worker\"", js);
        Assert.Contains("agentType: \"expert\"", js);
        Assert.Contains("model: \"haiku\"", js);
        Assert.Contains("model: \"opus\"", js);
        Assert.Contains("'blocked'", js);
        Assert.Contains("pipeline(", js);
        Assert.DoesNotContain("reviewer", js);
    }

    [Fact] public void Segment2ReadsGateEvidenceAndCallsReviewer()
    {
        var js = Js(2);
        Assert.Contains("args?.gates?.[\"batch-green\"]", js);
        Assert.Contains("args.state", js);
        Assert.Contains("agentType: \"reviewer\"", js);
        Assert.Contains("model: \"sonnet\"", js);
        Assert.Contains("Inspect the work on each listed branch", js);
        Assert.DoesNotContain("Process these completed tasks", js);
        Assert.DoesNotContain("agentType: \"worker\"", js);
        Assert.DoesNotContain("agentType: \"expert\"", js);
        Assert.DoesNotContain("args.tasks", js);
    }

    [Fact] public void FlowWithoutGatesOrToolsIsOneSegment()
    {
        var s = WithFlow(new Stage(StageType.Fanout, "worker"), new Stage(StageType.Role, "reviewer"));
        Assert.Equal(new[] { ".claude/workflows/epic-delivery.1.js", ".claude/workflows/epic-delivery.steps.md" }, Files(s).Keys);
        Assert.DoesNotContain("args?.gates", Js(1, s));
        Assert.Contains("agentType: \"reviewer\"", Js(1, s));
    }

    [Fact] public void FlowStartingWithGateGatesTheFirstSegmentButReadsTasks()
    {
        var s = WithFlow(new Stage(StageType.Gate, "batch-green"), new Stage(StageType.Fanout, "worker"));
        var js = Js(1, s);
        Assert.Contains("args?.gates?.[\"batch-green\"]", js);
        Assert.Contains("args.tasks", js);
        Assert.DoesNotContain("args.state", js);
        Assert.Contains("\"gates\": { \"batch-green\"", Steps(s));
    }

    [Fact] public void ConsecutiveGatesAndToolsDoNotCreateEmptySegments()
    {
        var s = WithFlow(new Stage(StageType.Fanout, "worker"), new Stage(StageType.Tool, "squash"), new Stage(StageType.Gate, "batch-green"), new Stage(StageType.Tool, "squash"));
        Assert.Single(Files(s).Keys, k => k.EndsWith(".js"));
    }

    [Fact] public void ToolOnlyFlowHasNoScripts()
    {
        var files = Files(WithFlow(new Stage(StageType.Tool, "squash")));
        Assert.Equal(new[] { ".claude/workflows/epic-delivery.steps.md" }, files.Keys);
    }

    [Fact] public void RunbookIsOrderedPinnedLfAndHasNoYes()
    {
        var md = Steps();
        Assert.Equal("Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.", md.Split('\n')[0]);
        Assert.Contains("dnx Swarm.Squash@0.1.0", md);
        Assert.DoesNotContain("--yes", md);
        Assert.DoesNotContain('\r', md);
        Assert.Contains("dnx.cmd", md);
        Assert.Contains(".docs/runs/gates/batch-green.json", md);
        Assert.Contains("{\"green\": true|false, \"summary\": \"...\"}", md);
        var order = new[] { "Run workflow epic-delivery.1 with args: ", "Gate \"batch-green\" (kind test): run tool \"squash\"", "Run workflow epic-delivery.2 with args: ", "Run: dnx Swarm.Squash@0.1.0" }
            .Select(x => md.IndexOf(x, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("\"tasks\": [", md);
        Assert.Contains("\"state\": <state from the previous workflow>", md);
        Assert.Contains("args.gates[\"batch-green\"]", md);
    }

    [Fact] public void ToolArgsAppearInRunbook()
    {
        var s = Sample() with { Tools = [new ToolDef("squash", "Swarm.Squash", "0.1.0", ["--base", "origin/main", "--dry-run=true"])] };
        Assert.Contains("Run: dnx Swarm.Squash@0.1.0 --base origin/main --dry-run=true", Steps(s));
    }

    [Theory]
    [InlineData("two words")] [InlineData("a\nb")] [InlineData("$(x)")] [InlineData("a;b")] [InlineData("")] [InlineData("a\"b")]
    public void UnsafeToolArgsAreRejected(string arg)
    {
        var s = Sample() with { Tools = [new ToolDef("squash", "Swarm.Squash", "0.1.0", [arg])] };
        Assert.Contains("unsafe", Msg(() => WorkflowRenderer.Render(s)));
    }

    [Fact] public void GateWithoutToolIsDescribedWithoutCommand()
    {
        var s = Sample() with { Gates = [new Gate("batch-green", "manual", null)] };
        var md = Steps(s);
        Assert.Contains("Gate \"batch-green\" (kind manual)", md);
        Assert.DoesNotContain("run tool", md);
        Assert.Contains(".docs/runs/gates/batch-green.json", md);
    }

    [Fact] public void RenderIsDeterministic()
    {
        var a = Files(); var b = Files();
        Assert.Equal(a.Keys, b.Keys);
        Assert.All(a, p => Assert.Equal(p.Value, b[p.Key]));
    }

    [Theory] [InlineData("a b")] [InlineData("../x")] [InlineData("CON")] [InlineData("")]
    public void UnsafeSwarmNameIsRejected(string name) =>
        Assert.Contains("not a safe file name", Msg(() => WorkflowRenderer.Render(Sample() with { Name = name })));

    [Fact] public void MissingRoleModelIsRejected()
    {
        var s = Sample() with { Roles = Sample().Roles.Select(r => r.Name == "worker" ? r with { Model = null } : r).ToList() };
        Assert.Contains("model", Msg(() => WorkflowRenderer.Render(s)));
    }

    [Fact] public void UnknownFlowTargetIsRejected() =>
        Assert.Contains("nope", Msg(() => WorkflowRenderer.Render(WithFlow(new Stage(StageType.Fanout, "nope")))));

    static readonly string Evil = "q" + (char)34 + "uote " + (char)92 + " back" + (char)96 + "tick ${evil} </script> line1" + (char)10 + "line2 sep" + (char)0x2028 + "end caf" + (char)0xe9 + " " + char.ConvertFromUtf32(0x1F600) + " tab" + (char)9 + " nul" + (char)0 + " del" + (char)0x7f;

    [Fact] public void HostileDescriptionIsRenderedAsInertAsciiLiteral()
    {
        var s = Sample() with { Description = Evil };
        foreach (var js in AllScripts(s))
        {
            Assert.All(js, c => Assert.True(c == '\n' || (c >= ' ' && c < 127)));
            var first = js.Split('\n')[0];
            Assert.DoesNotContain((char)0x2028, js);

            // with the double-quoted literals blanked, the meta line holds nothing from the hostile text
            var outside = System.Text.RegularExpressions.Regex.Replace(first, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
            Assert.DoesNotContain("${", outside);
            Assert.DoesNotContain("`", outside);
            Assert.DoesNotContain("evil", outside);
            Assert.DoesNotContain("evil", js.Replace(first, ""));
            Assert.Contains("\\u2028", first);
            Assert.Contains("\\\"", first);
        }

        if (NodeAvailable()) foreach (var js in AllScripts(s)) AssertNodeCheck(js);
    }

    [Fact] public void HostileModelStaysInsideLiteral()
    {
        var s = Sample();
        s = s with { Roles = s.Roles.Select(r => r.Name == "reviewer" ? r with { Model = "x\"; evil(); \"" } : r).ToList() };
        var js = Js(2, s);
        Assert.Contains("model: \"x\\\"; evil(); \\\"\"", js);
        if (NodeAvailable()) AssertNodeCheck(js);
    }

    static bool NodeAvailable() => FindNode() is not null;

    static string? FindNode() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(p => new[] { Path.Combine(p, "node.exe"), Path.Combine(p, "node") })
        .FirstOrDefault(File.Exists);

    static (int Exit, string Out, string Err) RunNode(params string[] args)
    {
        var psi = new ProcessStartInfo(FindNode()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    static void AssertNodeCheck(string js)
    {
        var f = TempFile(".mjs", js);
        try
        {
            var (exit, _, err) = RunNode("--check", f);
            Assert.True(exit == 0, err);
        }
        finally { File.Delete(f); }
    }

    [Fact] public void NodeIsAvailableOnThisMachine() => Assert.True(NodeAvailable(), "node not found on PATH; node-based tests were skipped");

    [Fact] public void EveryGeneratedScriptPassesNodeSyntaxCheck()
    {
        if (!NodeAvailable()) return;
        foreach (var js in AllScripts()) AssertNodeCheck(js);
        foreach (var js in AllScripts(WithFlow(new Stage(StageType.Gate, "batch-green"), new Stage(StageType.Role, "reviewer"), new Stage(StageType.Fanout, "worker")))) AssertNodeCheck(js);
    }

    // Runs a generated script under node with stub workflow hooks; the script's final log line is its JSON result.
    const string Harness = """
        import fs from 'node:fs';
        const [, , file, argsJson, mode] = process.argv;
        const src = fs.readFileSync(file, 'utf8').replace('export const meta =', 'const meta =');
        const calls = [], logs = [];
        const agent = async (prompt, opts) => {
          calls.push({ prompt, agentType: opts.agentType });
          if (mode === 'blocked' && opts.agentType === 'worker') return { status: 'blocked', branch: 'b', notes: 'stuck' };
          if (mode === 'failed') return null;
          return { status: 'done', branch: 'br-' + calls.length, notes: '' };
        };
        const pipeline = async (items, ...stages) => Promise.all(items.map(async (it, i) => { let r = it; for (const s of stages) r = await s(r, it, i); return r; }));
        const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
        await new AsyncFunction('agent', 'pipeline', 'parallel', 'phase', 'log', 'args', src)(agent, pipeline, async t => Promise.all(t.map(f => f())), () => {}, m => logs.push(String(m)), JSON.parse(argsJson));
        console.log(JSON.stringify({ calls, logs }));
        """;

    static (List<(string AgentType, string Prompt)> Calls, JsonElement Result) RunScript(string js, string argsJson, string mode = "ok")
    {
        var h = TempFile(".mjs", Harness); var f = TempFile(".mjs", js);
        try
        {
            var (exit, o, e) = RunNode(h, f, argsJson, mode);
            Assert.True(exit == 0, e);
            var root = JsonDocument.Parse(o).RootElement;
            var calls = root.GetProperty("calls").EnumerateArray().Select(c => (c.GetProperty("agentType").GetString()!, c.GetProperty("prompt").GetString()!)).ToList();
            var last = root.GetProperty("logs").EnumerateArray().Last().GetString()!;
            return (calls, JsonDocument.Parse(last).RootElement.Clone());
        }
        finally { File.Delete(h); File.Delete(f); }
    }

    [Fact] public void Segment2HaltsWithoutAgentCallsWhenGateEvidenceIsMissing()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ] }""");
        Assert.Empty(calls);
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal(0, r.GetProperty("state").GetArrayLength());
        Assert.Equal(0, r.GetProperty("unresolved").GetArrayLength());
        Assert.Contains("batch-green", r.GetProperty("reason").GetString());
    }

    [Fact] public void Segment2HaltsWhenGateIsRed()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done" } ], "gates": { "batch-green": { "green": false, "summary": "x" } } }""");
        Assert.Empty(calls);
        Assert.True(r.GetProperty("halted").GetBoolean());
    }

    [Fact] public void Segment2CallsReviewerOnceWhenGateGreen()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" }, { "status": "done", "branch": "b2" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""");
        var c = Assert.Single(calls);
        Assert.Equal("reviewer", c.AgentType);
        Assert.Contains("\"branch\":\"b1\"", c.Prompt);
        Assert.Contains("Inspect the work on each listed branch", c.Prompt);
        Assert.False(r.GetProperty("halted").GetBoolean());
        Assert.Equal(2, r.GetProperty("state").GetArrayLength());
    }

    [Fact] public void Segment1RunsWorkersAndReturnsDoneState()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(1), """{ "tasks": [ "t1", "t2" ] }""");
        Assert.Equal(new[] { "worker", "worker" }, calls.Select(i => i.AgentType));
        Assert.Contains("t1", calls[0].Prompt);
        Assert.False(r.GetProperty("halted").GetBoolean());
        Assert.Equal(2, r.GetProperty("state").GetArrayLength());
    }

    [Fact] public void Segment1RunsExpertOnBlockedResultUnderNode()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(1), """{ "tasks": [ "t1" ] }""", "blocked");
        Assert.Equal(new[] { "worker", "expert" }, calls.Select(i => i.AgentType));
        Assert.Contains("stuck", calls[1].Prompt);
        Assert.Equal(1, r.GetProperty("state").GetArrayLength());
    }

    [Fact] public void Segment1HaltsWhenEveryTaskFails()
    {
        if (!NodeAvailable()) return;
        var (_, r) = RunScript(Js(1), """{ "tasks": [ "t1", "t2" ] }""", "failed");
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal(0, r.GetProperty("state").GetArrayLength());
        Assert.Equal(2, r.GetProperty("unresolved").GetArrayLength());
    }
}
