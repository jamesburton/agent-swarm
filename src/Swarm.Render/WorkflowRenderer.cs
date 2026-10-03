using System.Text;
using System.Text.RegularExpressions;
using Swarm.Core;

namespace Swarm.Render;

/// <summary>
/// Renders a swarm flow as Workflow-tool scripts plus a runbook for the deterministic steps.
/// The flow is split at every Gate and Tool stage: each maximal run of Fanout/Role stages becomes one script,
/// and Gate/Tool stages become runbook steps because a workflow script cannot execute commands.
/// </summary>
public static partial class WorkflowRenderer
{
    // Characters allowed in a runbook command word; keeps commands copy-paste safe (no spaces, quotes, shell metacharacters).
    [GeneratedRegex(@"^[A-Za-z0-9_./:=@+,-]+\z")]
    private static partial Regex SafeArg();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\z")]
    private static partial Regex SafeWord();

    // Fan-out result: status is one of three values the prompt explains; base is what the reviewer diffs against.
    const string ResultSchema = "{ type: 'object', properties: { status: { type: 'string', enum: ['done', 'blocked', 'failed'] }, branch: { type: 'string' }, base: { type: 'string' }, notes: { type: 'string' } }, required: ['status'] }";

    // Role-stage (review) result: anything but an explicit approve halts the segment.
    const string VerdictSchema = "{ type: 'object', properties: { verdict: { type: 'string', enum: ['approve', 'reject'] }, notes: { type: 'string' } }, required: ['verdict', 'notes'] }";

    // Appended to every fan-out prompt (inside a JS template literal: no backticks, no "${").
    const string StatusGuide = "\\n\\nFinish with status 'done' (the task is finished and committed on your branch), 'blocked' (you cannot finish it; a stronger agent may continue from your notes) or 'failed' (it cannot be done as specified), and report your branch, the base it starts from, and short notes.";

    const string EvidenceShape = "{\"green\": true|false, \"summary\": \"...\"}";

    sealed record Segment(int Number, IReadOnlyList<string> Gates, List<Stage> Stages);

    /// <summary>Renders the workflow scripts and the tool-steps runbook.</summary>
    /// <param name="s">The swarm definition.</param>
    /// <returns>Relative path (<c>.claude/workflows/&lt;name&gt;.&lt;n&gt;.js</c> per segment, then <c>.claude/workflows/&lt;name&gt;.steps.md</c>) to file content, in that order.</returns>
    /// <exception cref="SwarmException">Thrown when the swarm name is not a safe file stem, a flow target or gate tool is missing, a role has no model, a Role stage would be first in the first segment, or a runbook command word is unsafe.</exception>
    public static IReadOnlyDictionary<string, string> Render(SwarmDefinition s)
    {
        SafeStems.Validate([s.Name], "swarm");
        var gateNames = s.Flow.Where(f => f.Type == StageType.Gate).Select(f => f.Target).Distinct().ToList();
        foreach (var g in gateNames) SafeStems.Validate([g], "gate");

        var segments = new List<Segment>();
        var steps = new List<string>();
        Segment? current = null;
        var pendingGates = new List<string>();
        for (var i = 0; i < s.Flow.Count; i++)
        {
            var st = s.Flow[i];
            switch (st.Type)
            {
                case StageType.Fanout or StageType.Role:
                    if (current is null)
                    {
                        current = new Segment(segments.Count + 1, pendingGates.ToList(), []);
                        pendingGates.Clear();
                        segments.Add(current);
                        if (current.Number == 1 && st.Type == StageType.Role)
                            throw new SwarmException($"role '{SafeText.Show(st.Target)}': a Role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work)");
                    }

                    current.Stages.Add(st);

                    // The workflow step sits at the position of its segment's first stage; its text is built once the segment is complete.
                    if (current.Stages.Count == 1) steps.Add(string.Empty);
                    break;
                case StageType.Gate:
                    current = null;
                    pendingGates.Add(st.Target);
                    steps.Add(GateStep(s, st.Target, i + 1 < s.Flow.Count && s.Flow[i + 1].Type is StageType.Fanout or StageType.Role));
                    break;
                case StageType.Tool:
                    current = null;
                    steps.Add("Run: " + Command(FindTool(s, st.Target)));
                    break;
            }
        }

        // Fill the workflow steps (placeholders) in segment order.
        var slot = 0;
        foreach (var seg in segments)
        {
            while (steps[slot].Length > 0) slot++;
            steps[slot] = RunStep(s, seg);
        }

        var files = new RenderedFiles();
        foreach (var seg in segments) files.Add($".claude/workflows/{s.Name}.{seg.Number}.js", RenderScript(s, seg));
        files.Add($".claude/workflows/{s.Name}.steps.md", RenderRunbook(s.Name, steps));
        return files;
    }

    // ---- runbook ----

    static string RenderRunbook(string name, List<string> steps)
    {
        var sb = new StringBuilder();
        sb.Append(GeneratedMarker.MarkdownLine).Append('\n');
        sb.Append("Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.\n");
        sb.Append("In Git Bash write `dnx.cmd` instead of `dnx`.\n");
        sb.Append($"Save each workflow result to `.docs/runs/{name}.<n>.result.json`; pass the `state` field of the previous workflow's result as the next workflow's `args.state`; tool steps that need the task list read the latest such file.\n\n");
        for (var i = 0; i < steps.Count; i++) sb.Append(i + 1).Append(". ").Append(steps[i]).Append('\n');
        return sb.ToString();
    }

    static string RunStep(SwarmDefinition s, Segment seg)
    {
        var id = $"{s.Name}.{seg.Number}";
        var sb = new StringBuilder($"Run workflow `{id}` (file `.claude/workflows/{id}.js`; pass scriptPath if lookup by name is unavailable) with args: {{ ");
        sb.Append(seg.Number == 1 ? "\"tasks\": [ \"<task 1>\", \"<task 2>\" ]" : "\"state\": <the `state` field of the previous workflow's result>");
        if (seg.Gates.Count > 0)
            sb.Append(", \"gates\": { ").Append(string.Join(", ", seg.Gates.Distinct().Select(g => $"\"{g}\": <evidence from {GatePath(g)}>"))).Append(" }");
        sb.Append(" }. ");
        sb.Append($"Save the result to `.docs/runs/{id}.result.json`. ");
        sb.Append("If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved (and pending); do not run later steps.");
        if (seg.Stages.Any(st => st.Type == StageType.Role)) sb.Append(" A reject verdict from a review stage also halts the workflow, so this STOP rule covers it.");
        return sb.ToString();
    }

    static string GatePath(string gate) => $".docs/runs/gates/{gate}.json";

    static string GateStep(SwarmDefinition s, string name, bool checkedByNextWorkflow)
    {
        var g = s.Gates.FirstOrDefault(x => x.Name == name) ?? throw new SwarmException($"flow stage '{SafeText.Show(name)}' (Gate) does not exist");
        var kind = Word(g.Kind, $"gate '{name}'", "kind");
        var how = g.Tool is null
            ? "no tool is attached; obtain the evidence yourself"
            : "run " + Command(s.Tools.FirstOrDefault(t => t.Name == g.Tool) ?? throw new SwarmException($"gate '{SafeText.Show(name)}': tool '{SafeText.Show(g.Tool)}' does not exist"));
        var sb = new StringBuilder($"Gate \"{name}\" (kind {kind}): {how} and write the evidence JSON {EvidenceShape} to {GatePath(name)}, then pass it as args.gates[\"{name}\"]. If green is false: STOP and do not run later steps.");
        if (!checkedByNextWorkflow) sb.Append(" No workflow checks this gate, so this STOP rule is the only guard for the steps that follow.");
        return sb.ToString();
    }

    static ToolDef FindTool(SwarmDefinition s, string name) =>
        s.Tools.FirstOrDefault(x => x.Name == name) ?? throw new SwarmException($"flow stage '{SafeText.Show(name)}' (Tool) does not exist");

    // The single command builder for tool and gate steps. Arguments follow a '--' separator so flags stay away
    // from dnx's own parser (the separator's pass-through semantics are UNVERIFIED; Task 8 verifies them).
    static string Command(ToolDef t)
    {
        var who = $"tool '{SafeText.Show(t.Name)}'";
        if (!Validator.IsPinnedVersion(t.Version)) throw new SwarmException($"{who}: version '{SafeText.Show(t.Version ?? "")}' is not an exact pinned version");
        var words = new List<string> { "dnx", Arg(t.Package, who, "package") + "@" + Arg(t.Version, who, "version") };
        if (t.Args.Count > 0)
        {
            words.Add("--");
            foreach (var a in t.Args)
            {
                if (a.Equals("--yes", StringComparison.OrdinalIgnoreCase) || a.Equals("-y", StringComparison.OrdinalIgnoreCase))
                    throw new SwarmException($"{who}: argument '{a}' is not allowed (dnx must not auto-confirm)");
                words.Add(Arg(a, who, "argument"));
            }
        }

        return string.Join(' ', words);
    }

    static string Arg(string value, string who, string what) =>
        SafeArg().IsMatch(value) ? value : throw new SwarmException($"{who}: {what} '{SafeText.Show(value)}' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)");

    static string Word(string value, string who, string what) =>
        SafeWord().IsMatch(value) ? value : throw new SwarmException($"{who}: {what} '{SafeText.Show(value)}' is unsafe (allowed characters: letters, digits and _ . -)");

    // ---- scripts ----

    // Every script is a flat body ending in a top-level return: the real Workflow tool returns that value
    // (a main() wrapper with a trailing expression returns nothing).
    static string RenderScript(SwarmDefinition s, Segment seg)
    {
        var phases = new List<(string Title, string? Model)>();
        foreach (var g in seg.Gates) phases.Add((GatePhase(g), null));
        foreach (var st in seg.Stages)
        {
            phases.Add((st.Target, null));
            if (st.Type == StageType.Fanout && FindRole(s, st.Target).EscalateTo is { } to) phases.Add((to, FindRole(s, to).Model));
        }

        var meta = phases.GroupBy(p => p.Title).Select(g => g.First())
            .Select(p => p.Model is null ? $"{{ title: {Lit(p.Title)} }}" : $"{{ title: {Lit(p.Title)}, model: {Lit(p.Model)} }}");
        var sb = new StringBuilder();
        sb.Append("export const meta = { name: ").Append(Lit($"{s.Name}.{seg.Number}"))
          .Append(", description: ").Append(Lit(s.Description))
          .Append(", phases: [").Append(string.Join(", ", meta)).Append("] };\n");
        sb.Append(GeneratedMarker.ScriptLine).Append("\n\n");
        sb.Append("// Generated from the swarm definition; do not edit.\n");
        sb.Append(seg.Number == 1
            ? "// Input: args.tasks, an array of task descriptions.\n"
            : "// Input: args.state, the state array of the previous workflow's result.\n");
        if (seg.Gates.Count > 0) sb.Append("// Gate evidence: args.gates[<gate>] = { green, summary }, produced by the main session.\n");
        sb.Append("// Result: { halted, state, unresolved, pending, reviews }; state holds only results with status 'done'; reviews holds { role, verdict, notes }.\n");
        if (seg.Stages.Any(st => st.Type == StageType.Fanout)) sb.Append("const RESULT = ").Append(ResultSchema).Append(";\n");
        if (seg.Stages.Any(st => st.Type == StageType.Role)) sb.Append("const VERDICT = ").Append(VerdictSchema).Append(";\n");
        sb.Append('\n');

        sb.Append(seg.Number == 1
            ? "let state = args && Array.isArray(args.tasks) ? args.tasks : [];\n"
            : "let state = args && Array.isArray(args.state) ? args.state : [];\n");
        sb.Append("const received = state;\nconst unresolved = [];\nconst reviews = [];\n");
        sb.Append("const halt = (reason, pending = received) => {\n  log(reason);\n  return { halted: true, state: [], unresolved, pending, reviews, reason };\n};\n");

        foreach (var g in seg.Gates.Distinct())
        {
            sb.Append($"\nphase({Lit(GatePhase(g))});\n");
            sb.Append($"if (!(args?.gates?.[{Lit(g)}]?.green === true)) return halt({Lit($"gate {g}: evidence is missing or not green")});\n");
        }

        for (var i = 0; i < seg.Stages.Count; i++)
        {
            var st = seg.Stages[i];
            var role = FindRole(s, st.Target);
            sb.Append("\n{\n");
            sb.Append($"  phase({Lit(st.Target)});\n");
            if (st.Type == StageType.Fanout) AppendFanout(sb, s, role, raw: seg.Number == 1 && i == 0);
            else AppendRole(sb, role);
            sb.Append("}\n");
        }

        sb.Append("\nreturn { halted: false, state, unresolved, pending: [], reviews };\n");
        return sb.ToString();
    }

    static string GatePhase(string gate) => $"gate {gate}";

    static void AppendFanout(StringBuilder sb, SwarmDefinition s, Role role, bool raw)
    {
        var prompt = raw
            ? "Task: ${typeof t === 'string' ? t : JSON.stringify(t)}"
            : "Work item from the previous stage: ${JSON.stringify(t)}. Do your role's work on it.";
        sb.Append("  const items = state;\n  const results = await pipeline(\n    items,\n");
        sb.Append($"    (t) => agent(`{prompt}{StatusGuide}`, {AgentOptions(role, "RESULT")}),\n");
        if (role.EscalateTo is { } to)
        {
            var expert = FindRole(s, to);
            sb.Append("    (res, t) => res && res.status === 'blocked'\n");
            sb.Append("      ? agent(`Task: ${JSON.stringify(t)}. A previous agent could not finish it and returned this result: ${JSON.stringify(res)}. Continue from that hand-off; do not repeat what it already tried.").Append(StatusGuide).Append("`, ");
            sb.Append(AgentOptions(expert, "RESULT")).Append(")\n      : res,\n");
        }

        sb.Append("  );\n");
        sb.Append("  const settled = results.map((r, i) => ({ r, task: items[i] }));\n");
        sb.Append("  unresolved.push(...settled.filter((x) => x.r?.status !== 'done').map((x) => ({ ...(x.r ?? { status: 'failed', notes: 'agent returned no result' }), task: x.task })));\n");
        sb.Append("  state = settled.filter((x) => x.r?.status === 'done').map((x) => x.r);\n");
        sb.Append($"  if (state.length === 0) return halt({Lit($"{role.Name}: no task finished with status done")});\n");
    }

    static void AppendRole(StringBuilder sb, Role role)
    {
        sb.Append($"  if (state.length === 0) return halt({Lit($"{role.Name}: there are no completed tasks to work on")});\n");
        sb.Append("  const out = await agent(`You are working on these completed tasks: ${JSON.stringify(state)}. Inspect the work on each listed branch (its diff against that task's reported base) and carry out your role on it. Finish with verdict 'approve' if the work may proceed, or 'reject' with the blocking issues in notes; a reject stops the run.`, ");
        sb.Append(AgentOptions(role, "VERDICT")).Append(");\n");

        // Anything but an explicit approve (including no answer at all) is a reject, and a reject halts with the reviewed work pending.
        sb.Append("  const verdict = out?.verdict === 'approve' ? 'approve' : 'reject';\n");
        sb.Append("  const notes = typeof out?.notes === 'string' ? out.notes : (out ? '' : 'agent returned no verdict');\n");
        sb.Append($"  reviews.push({{ role: {Lit(role.Name)}, verdict, notes }});\n");
        sb.Append($"  if (verdict !== 'approve') return halt({Lit($"{role.Name} rejected the work: ")} + notes, state);\n");
    }

    // Options for one agent() call: the role's agent type, model and phase, its effort and isolation when set, and the result schema.
    static string AgentOptions(Role role, string schema)
    {
        if (string.IsNullOrWhiteSpace(role.Model)) throw new SwarmException($"role '{SafeText.Show(role.Name)}': model is missing");
        var opts = new List<string> { $"agentType: {Lit(role.Name)}", $"model: {Lit(role.Model)}", $"phase: {Lit(role.Name)}" };
        if (role.Effort is { } e) opts.Add($"effort: {Lit(e)}");
        if (role.Isolation is { } i) opts.Add($"isolation: {Lit(i)}");
        opts.Add($"schema: {schema}");
        return $"{{ {string.Join(", ", opts)} }}";
    }

    static Role FindRole(SwarmDefinition s, string name) =>
        s.Roles.FirstOrDefault(r => r.Name == name && r.Kind == RoleKind.Llm)
        ?? throw new SwarmException($"flow stage '{SafeText.Show(name)}' does not name an llm role");

    // The single place definition data becomes JS source: a double-quoted literal, ASCII only, so it can never
    // end the string, start a template expression, or smuggle a control or line-separator character.
    static string Lit(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
