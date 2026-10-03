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
            Assert.EndsWith("};", first);
            Assert.DoesNotContain("`", first);
            Assert.DoesNotContain("${", first);
            Assert.DoesNotContain("(", first);
        }

        Assert.Contains("name: \"epic-delivery.1\"", Js(1));
        Assert.Contains("phases: [{ title: \"worker\" }, { title: \"expert\", model: \"opus\" }]", Js(1));
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
        Assert.Equal(GeneratedMarker.MarkdownLine, md.Split('\n')[0]);
        Assert.Equal("Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.", md.Split('\n')[1]);
        Assert.Contains("dnx Swarm.Squash@0.1.0", md);
        Assert.DoesNotContain("--yes", md);
        Assert.DoesNotContain('\r', md);
        Assert.Contains("dnx.cmd", md);
        Assert.Contains(".docs/runs/gates/batch-green.json", md);
        Assert.Contains("{\"green\": true|false, \"summary\": \"...\"}", md);
        var order = new[] { "Run workflow `epic-delivery.1` (file `.claude/workflows/epic-delivery.1.js`; pass scriptPath if lookup by name is unavailable) with args: ", "Gate \"batch-green\" (kind test): run dnx Swarm.TestGate@0.1.0 and write", "Run workflow `epic-delivery.2` (file", "Run: dnx Swarm.Squash@0.1.0" }
            .Select(x => md.IndexOf(x, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("\"tasks\": [", md);
        Assert.Contains("\"state\": <the `state` field of the previous workflow's result>", md);
        Assert.Contains("args.gates[\"batch-green\"]", md);
    }

    [Fact] public void ToolArgsAppearInRunbook()
    {
        Assert.Contains("Run: dnx Swarm.Squash@0.1.0 -- --base origin/main --dry-run=true", Steps(WithToolArgs("--base", "origin/main", "--dry-run=true")));
    }

    [Theory]
    [InlineData("two words")] [InlineData("a\nb")] [InlineData("$(x)")] [InlineData("a;b")] [InlineData("")] [InlineData("a\"b")]
    public void UnsafeToolArgsAreRejected(string arg)
    {
        Assert.Contains("unsafe", Msg(() => WorkflowRenderer.Render(WithToolArgs(arg))));
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

    static bool NodeAvailable() => NodeScripts.Required();

    static void AssertNodeCheck(string js) => NodeScripts.AssertNodeCheck(js);

    static (List<(string AgentType, string Prompt, JsonElement Opts)> Calls, JsonElement Result) RunScript(string js, string argsJson, string mode = "ok") =>
        NodeScripts.RunScript(js, argsJson, mode);

    [Fact] public void NodeHelperFailsLoudlyWhenNodeIsMissing()
    {
        // An empty PATH has no node: without the opt-out the helper must fail, not pass vacuously.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => NodeScripts.Required("", null));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => NodeScripts.Required("", "0"));
        Assert.False(NodeScripts.Required("", "1"));
    }

    [Fact] public void NodeCheckRejectsBrokenScript()
    {
        if (!NodeAvailable()) return;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNodeCheck("export const meta = { name: \"x\" };\nreturn { halted: ;\n"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNodeCheck("export const meta = { name: ;\nreturn {};\n"));
    }

    [Fact] public void EveryGeneratedScriptPassesNodeSyntaxCheck()
    {
        if (!NodeAvailable()) return;
        foreach (var js in AllScripts()) AssertNodeCheck(js);
        foreach (var js in AllScripts(WithFlow(new Stage(StageType.Gate, "batch-green"), new Stage(StageType.Fanout, "worker"), new Stage(StageType.Role, "reviewer")))) AssertNodeCheck(js);
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

    static SwarmDefinition WithGateThenTool() =>
        Sample() with { Gates = [new Gate("g", "test", "squash")], Flow = [new Stage(StageType.Fanout, "worker"), new Stage(StageType.Gate, "g"), new Stage(StageType.Tool, "squash")] };

    // The sample with one tool edited (the other tools are kept, so the gate's tool still exists).
    static SwarmDefinition WithTool(string name, Func<ToolDef, ToolDef> edit) =>
        Sample() with { Tools = [.. Sample().Tools.Select(t => t.Name == name ? edit(t) : t)] };

    static SwarmDefinition WithToolArgs(params string[] args) => WithTool("squash", t => t with { Args = args });

    [Fact] public void ScriptsReturnAtTopLevelWithoutMainWrapper()
    {
        foreach (var js in AllScripts())
        {
            Assert.DoesNotContain("async function main", js);
            Assert.Contains("\nreturn { halted: false, state, unresolved, pending: [], reviews };\n", js);
        }
    }

    [Fact] public void Segment1MetaListsEscalationRoleWithItsModelOnce()
    {
        var s = WithFlow(new Stage(StageType.Fanout, "worker"), new Stage(StageType.Fanout, "worker"));
        Assert.Contains("phases: [{ title: \"worker\" }, { title: \"expert\", model: \"opus\" }] };", Js(1, s));
    }

    [Fact] public void RoleStageFirstInFirstSegmentIsRejected() =>
        Assert.Contains("cannot be the first stage", Msg(() => WorkflowRenderer.Render(WithFlow(new Stage(StageType.Role, "reviewer")))));

    [Fact] public void RunbookWorkflowStepsCarryStopAndSaveRules()
    {
        var steps = Steps().Split('\n').Where(l => l.Contains("Run workflow")).ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, l =>
        {
            Assert.Contains("If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved", l);
            Assert.Contains("do not run later steps", l);
        });
        Assert.Contains("`.docs/runs/epic-delivery.1.result.json`", steps[0]);
        Assert.Contains(" A reject verdict from a review stage also halts the workflow, so this STOP rule covers it.", steps[1]);
        Assert.DoesNotContain("reject", steps[0]);
        Assert.Contains("pass the `state` field of the previous workflow's result as the next workflow's `args.state`", Steps());
    }

    [Fact] public void RunbookGateStepStopsOnRedAndSaysWhetherItIsTheOnlyGuard()
    {
        var gate = Steps().Split('\n').Single(l => l.Contains("Gate \"batch-green\""));
        Assert.Contains("If green is false: STOP", gate);
        Assert.DoesNotContain("only guard", gate);

        var last = Steps(WithGateThenTool()).Split('\n');
        var g = last.Single(l => l.Contains("Gate \"g\""));
        Assert.Contains("If green is false: STOP", g);
        Assert.Contains("only guard for the steps that follow", g);
        Assert.True(Array.IndexOf(last, last.Single(l => l.Contains("Run: dnx"))) > Array.IndexOf(last, g));
    }

    [Fact] public void ToolArgsAreSeparatedWithDoubleDashAndToolWithoutArgsHasNone()
    {
        Assert.Contains("Run: dnx Swarm.Squash@0.1.0 -- --slots 2\n", Steps(WithToolArgs("--slots", "2")));
        Assert.Contains("Run: dnx Swarm.Squash@0.1.0\n", Steps());
    }

    [Theory] [InlineData("--yes")] [InlineData("-y")] [InlineData("--YES")]
    public void YesFlagInToolArgsIsRejected(string flag) =>
        Assert.Contains("not allowed", Msg(() => WorkflowRenderer.Render(WithToolArgs("--slots", flag))));

    [Fact] public void GateStepPrintsTheToolsPinnedCommandViaTheSameBuilder()
    {
        var md = Steps(WithTool("testgate", t => t with { Args = ["--slots", "2"] }));
        Assert.Contains("Gate \"batch-green\" (kind test): run dnx Swarm.TestGate@0.1.0 -- --slots 2 and write", md);
    }

    [Fact] public void GateWithMissingToolIsRejectedWithOneLine() =>
        Assert.Equal("gate 'batch-green': tool 'nope' does not exist", Msg(() => WorkflowRenderer.Render(Sample() with { Gates = [new Gate("batch-green", "test", "nope")] })));

    [Fact] public void UnpinnedToolVersionIsRejected() =>
        Assert.Contains("not an exact pinned version", Msg(() => WorkflowRenderer.Render(WithTool("squash", t => t with { Version = "latest" }))));

    [Fact] public void HostileGateAndToolNamesAndPackagesAreRejected()
    {
        Assert.Contains("not a safe file name", Msg(() => WorkflowRenderer.Render(Sample() with { Gates = [new Gate("a b\n$(x)", "test", "squash")], Flow = [new Stage(StageType.Fanout, "worker"), new Stage(StageType.Gate, "a b\n$(x)")] })));
        Assert.Contains("unsafe", Msg(() => WorkflowRenderer.Render(WithTool("squash", t => t with { Package = "Swarm.Squash; rm -rf /" }))));
        Assert.Contains("unsafe", Msg(() => WorkflowRenderer.Render(Sample() with { Gates = [new Gate("batch-green", "te st", "testgate")] })));
    }

    [Fact] public void MissingGateEvidenceReturnsHaltedResultWithPendingStateAndNoAgentCalls()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ] }""");
        Assert.Empty(calls);
        Assert.Equal("""{"halted":true,"state":[],"unresolved":[],"pending":[{"status":"done","branch":"b1"}],"reviews":[],"reason":"gate batch-green: evidence is missing or not green"}""", r.GetRawText());
    }

    [Fact] public void RedGateReturnsHaltedResult()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done" } ], "gates": { "batch-green": { "green": false, "summary": "x" } } }""");
        Assert.Empty(calls);
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal(1, r.GetProperty("pending").GetArrayLength());
    }

    [Fact] public void GreenGateReturnsReviewOutputAndEmptyPending()
    {
        if (!NodeAvailable()) return;
        var (_, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""");
        Assert.Equal("""{"halted":false,"state":[{"status":"done","branch":"b1"}],"unresolved":[],"pending":[],"reviews":[{"role":"reviewer","verdict":"approve","notes":"LGTM from reviewer"}]}""", r.GetRawText());
    }

    [Fact] public void WorkerAndExpertBothBlockedHaltsWithTaskOnUnresolved()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(1), """{ "tasks": [ "t1" ] }""", "allblocked");
        Assert.Equal(new[] { "worker", "expert" }, calls.Select(c => c.AgentType));
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal(0, r.GetProperty("state").GetArrayLength());
        var u = Assert.Single(r.GetProperty("unresolved").EnumerateArray());
        Assert.Equal("blocked", u.GetProperty("status").GetString());
        Assert.Equal("t1", u.GetProperty("task").GetString());
        Assert.Equal("t1", Assert.Single(r.GetProperty("pending").EnumerateArray()).GetString());
    }

    [Fact] public void NullResultsGetTaskOnUnresolved()
    {
        if (!NodeAvailable()) return;
        var (_, r) = RunScript(Js(1), """{ "tasks": [ "t1" ] }""", "failed");
        var u = Assert.Single(r.GetProperty("unresolved").EnumerateArray());
        Assert.Equal("failed", u.GetProperty("status").GetString());
        Assert.Equal("t1", u.GetProperty("task").GetString());
    }

    [Fact] public void FlowStartingWithGateHaltsWithoutEvidenceAndRunsWhenGreen()
    {
        if (!NodeAvailable()) return;
        var s = WithFlow(new Stage(StageType.Gate, "batch-green"), new Stage(StageType.Fanout, "worker"));
        var (calls, r) = RunScript(Js(1, s), """{ "tasks": [ "t1" ] }""");
        Assert.Empty(calls);
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal("t1", Assert.Single(r.GetProperty("pending").EnumerateArray()).GetString());

        (calls, r) = RunScript(Js(1, s), """{ "tasks": [ "t1" ], "gates": { "batch-green": { "green": true, "summary": "" } } }""");
        Assert.Equal(new[] { "worker" }, calls.Select(c => c.AgentType));
        Assert.False(r.GetProperty("halted").GetBoolean());
    }

    [Fact] public void TwoFanoutStagesInOneSegmentChainStateWithSensiblePrompt()
    {
        var s = WithFlow(new Stage(StageType.Fanout, "worker"), new Stage(StageType.Fanout, "expert"));
        Assert.Single(Files(s).Keys, k => k.EndsWith(".js"));
        Assert.Contains("Task: ${typeof t === 'string'", Js(1, s));
        Assert.Contains("Work item from the previous stage: ${JSON.stringify(t)}", Js(1, s));
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(1, s), """{ "tasks": [ "t1", "t2" ] }""");
        Assert.Equal(new[] { "worker", "worker", "expert", "expert" }, calls.Select(c => c.AgentType));
        Assert.StartsWith("Work item from the previous stage:", calls[2].Prompt);
        Assert.Equal(2, r.GetProperty("state").GetArrayLength());
    }

    [Fact] public void Segment1HaltsWhenEveryTaskFails()
    {
        if (!NodeAvailable()) return;
        var (_, r) = RunScript(Js(1), """{ "tasks": [ "t1", "t2" ] }""", "failed");
        Assert.True(r.GetProperty("halted").GetBoolean());
        Assert.Equal(0, r.GetProperty("state").GetArrayLength());
        Assert.Equal(2, r.GetProperty("unresolved").GetArrayLength());
    }

    // ---- final-review contract: status enum, verdicts, agent options, markers ----

    static string[] Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];

    [Fact] public void ResultSchemaRestrictsStatusAndCarriesBase()
    {
        var js = Js(1);
        Assert.Contains("status: { type: 'string', enum: ['done', 'blocked', 'failed'] }", js);
        Assert.Contains("base: { type: 'string' }", js);
        Assert.DoesNotContain("const VERDICT", js);
    }

    [Fact] public void FanoutPromptsExplainEveryStatusValueUnderNode()
    {
        if (!NodeAvailable()) return;
        var (calls, _) = RunScript(Js(1), """{ "tasks": [ "t1" ] }""", "blocked");
        Assert.Equal(new[] { "worker", "expert" }, calls.Select(c => c.AgentType));
        foreach (var (_, prompt, opts) in calls)
        {
            Assert.Equal(new[] { "done", "blocked", "failed" }, Strings(opts.GetProperty("schema").GetProperty("properties").GetProperty("status").GetProperty("enum")));
            Assert.Contains("status 'done' (the task is finished and committed on your branch)", prompt);
            Assert.Contains("'blocked' (you cannot finish it; a stronger agent may continue from your notes)", prompt);
            Assert.Contains("'failed' (it cannot be done as specified)", prompt);
            Assert.Contains("the base it starts from", prompt);
        }
    }

    [Fact] public void AgentOptionsCarryEffortAndIsolationUnderNode()
    {
        if (!NodeAvailable()) return;
        var (calls, _) = RunScript(Js(1), """{ "tasks": [ "t1" ] }""", "blocked");
        var worker = calls[0].Opts;
        Assert.Equal("low", worker.GetProperty("effort").GetString());
        Assert.Equal("worktree", worker.GetProperty("isolation").GetString());
        Assert.Equal("haiku", worker.GetProperty("model").GetString());
        var expert = calls[1].Opts;
        Assert.Equal("high", expert.GetProperty("effort").GetString());
        Assert.Equal("worktree", expert.GetProperty("isolation").GetString());

        (calls, _) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1", "base": "main" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""");
        var reviewer = Assert.Single(calls).Opts;
        Assert.Equal("medium", reviewer.GetProperty("effort").GetString());
        Assert.False(reviewer.TryGetProperty("isolation", out _));
    }

    [Fact] public void AgentOptionsOmitUnsetEffortAndIsolation()
    {
        var s = Sample();
        s = s with { Roles = [.. s.Roles.Select(r => r.Name == "worker" ? r with { Effort = null, Isolation = null } : r)] };
        Assert.Contains("{ agentType: \"worker\", model: \"haiku\", phase: \"worker\", schema: RESULT }", Js(1, s));
        Assert.Contains("{ agentType: \"expert\", model: \"opus\", phase: \"expert\", effort: \"high\", isolation: \"worktree\", schema: RESULT }", Js(1, s));
    }

    [Fact] public void ReviewerUsesVerdictSchemaAndIsToldTheBase()
    {
        var js = Js(2);
        Assert.Contains("const VERDICT = { type: 'object', properties: { verdict: { type: 'string', enum: ['approve', 'reject'] }, notes: { type: 'string' } }, required: ['verdict', 'notes'] };", js);
        Assert.Contains("{ agentType: \"reviewer\", model: \"sonnet\", phase: \"reviewer\", effort: \"medium\", schema: VERDICT }", js);
        Assert.Contains("its diff against that task's reported base", js);
        Assert.Contains("verdict 'approve'", js);
        Assert.Contains("'reject'", js);
        Assert.DoesNotContain("const RESULT", js);
    }

    [Fact] public void ReviewerApproveContinuesUnderNode()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""");
        Assert.Equal(new[] { "approve", "reject" }, Strings(Assert.Single(calls).Opts.GetProperty("schema").GetProperty("properties").GetProperty("verdict").GetProperty("enum")));
        Assert.False(r.GetProperty("halted").GetBoolean());
        var review = Assert.Single(r.GetProperty("reviews").EnumerateArray());
        Assert.Equal("approve", review.GetProperty("verdict").GetString());
    }

    [Fact] public void ReviewerRejectHaltsWithStateMovedToPendingUnderNode()
    {
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""", "reject");
        Assert.Single(calls);
        Assert.Equal("""{"halted":true,"state":[],"unresolved":[],"pending":[{"status":"done","branch":"b1"}],"reviews":[{"role":"reviewer","verdict":"reject","notes":"blocking: tests missing"}],"reason":"reviewer rejected the work: blocking: tests missing"}""", r.GetRawText());
    }

    [Fact] public void MissingVerdictCountsAsRejectUnderNode()
    {
        if (!NodeAvailable()) return;
        var (_, r) = RunScript(Js(2), """{ "state": [ { "status": "done", "branch": "b1" } ], "gates": { "batch-green": { "green": true, "summary": "ok" } } }""", "failed");
        Assert.True(r.GetProperty("halted").GetBoolean());
        var review = Assert.Single(r.GetProperty("reviews").EnumerateArray());
        Assert.Equal("reject", review.GetProperty("verdict").GetString());
        Assert.Equal("agent returned no verdict", review.GetProperty("notes").GetString());
        Assert.Equal("reviewer rejected the work: agent returned no verdict", r.GetProperty("reason").GetString());
    }

    [Fact] public void RejectAfterFanoutInTheSameSegmentPendsTheFinishedWorkUnderNode()
    {
        var s = WithFlow(new Stage(StageType.Fanout, "worker"), new Stage(StageType.Role, "reviewer"));
        if (!NodeAvailable()) return;
        var (calls, r) = RunScript(Js(1, s), """{ "tasks": [ "t1" ] }""", "reject");
        Assert.Equal(new[] { "worker", "reviewer" }, calls.Select(c => c.AgentType));
        Assert.True(r.GetProperty("halted").GetBoolean());
        var pending = Assert.Single(r.GetProperty("pending").EnumerateArray());
        Assert.Equal("done", pending.GetProperty("status").GetString());
    }

    [Fact] public void EveryGeneratedFileCarriesItsMarker()
    {
        foreach (var (key, text) in Files())
        {
            Assert.True(GeneratedMarker.IsPresent(text), key);
            var lines = text.Split('\n');
            if (key.EndsWith(".js"))
            {
                Assert.StartsWith("export const meta = ", lines[0]);
                Assert.Equal(GeneratedMarker.ScriptLine, lines[1]);
            }
            else Assert.Equal(GeneratedMarker.MarkdownLine, lines[0]);
        }
    }
}
