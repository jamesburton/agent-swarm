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

    const string ResultSchema = "{ type: 'object', properties: { status: { type: 'string' }, branch: { type: 'string' }, notes: { type: 'string' } }, required: ['status'] }";

    const string EvidenceShape = "{\"green\": true|false, \"summary\": \"...\"}";

    sealed record Segment(int Number, IReadOnlyList<string> Gates, List<Stage> Stages);

    /// <summary>Renders the workflow scripts and the tool-steps runbook.</summary>
    /// <param name="s">The swarm definition.</param>
    /// <returns>Relative path (<c>.claude/workflows/&lt;name&gt;.&lt;n&gt;.js</c> per segment, then <c>.claude/workflows/&lt;name&gt;.steps.md</c>) to file content, in that order.</returns>
    /// <exception cref="SwarmException">Thrown when the swarm name is not a safe file stem, a flow target is missing, a role has no model, or a runbook command word is unsafe.</exception>
    public static IReadOnlyDictionary<string, string> Render(SwarmDefinition s)
    {
        SafeStems.Validate([s.Name], "swarm");
        var gateNames = s.Flow.Where(f => f.Type == StageType.Gate).Select(f => f.Target).Distinct().ToList();
        foreach (var g in gateNames) SafeStems.Validate([g], "gate");

        var segments = new List<Segment>();
        var steps = new List<string>();
        Segment? current = null;
        var pendingGates = new List<string>();
        foreach (var st in s.Flow)
        {
            switch (st.Type)
            {
                case StageType.Fanout or StageType.Role:
                    if (current is null)
                    {
                        current = new Segment(segments.Count + 1, pendingGates.ToList(), []);
                        pendingGates.Clear();
                        segments.Add(current);
                        steps.Add(RunStep(s.Name, current));
                    }

                    current.Stages.Add(st);
                    break;
                case StageType.Gate:
                    current = null;
                    pendingGates.Add(st.Target);
                    steps.Add(GateStep(s, st.Target));
                    break;
                case StageType.Tool:
                    current = null;
                    steps.Add(ToolStep(s, st.Target));
                    break;
            }
        }

        var files = new RenderedFiles();
        foreach (var seg in segments) files.Add($".claude/workflows/{s.Name}.{seg.Number}.js", RenderScript(s, seg));
        files.Add($".claude/workflows/{s.Name}.steps.md", RenderRunbook(steps));
        return files;
    }

    // ---- runbook ----

    static string RenderRunbook(List<string> steps)
    {
        var sb = new StringBuilder();
        sb.Append("Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.\n");
        sb.Append("In Git Bash write `dnx.cmd` instead of `dnx`.\n\n");
        for (var i = 0; i < steps.Count; i++) sb.Append(i + 1).Append(". ").Append(steps[i]).Append('\n');
        return sb.ToString();
    }

    static string RunStep(string swarm, Segment seg)
    {
        var sb = new StringBuilder($"Run workflow {swarm}.{seg.Number} with args: {{ ");
        sb.Append(seg.Number == 1 ? "\"tasks\": [ \"<task 1>\", \"<task 2>\" ]" : "\"state\": <state from the previous workflow>");
        if (seg.Gates.Count > 0)
            sb.Append(", \"gates\": { ").Append(string.Join(", ", seg.Gates.Distinct().Select(g => $"\"{g}\": <evidence from {GatePath(g)}>"))).Append(" }");
        return sb.Append(" }").ToString();
    }

    static string GatePath(string gate) => $".docs/runs/gates/{gate}.json";

    static string GateStep(SwarmDefinition s, string name)
    {
        var g = s.Gates.FirstOrDefault(x => x.Name == name) ?? throw new SwarmException($"flow stage '{SafeStems.Show(name)}' (Gate) does not exist");
        var kind = Word(g.Kind, $"gate '{name}'", "kind");
        var how = g.Tool is null
            ? "no tool is attached; obtain the evidence yourself"
            : $"run tool \"{Word(g.Tool, $"gate '{name}'", "tool")}\"";
        return $"Gate \"{name}\" (kind {kind}): {how} and write the evidence JSON {EvidenceShape} to {GatePath(name)}, then pass it as args.gates[\"{name}\"]";
    }

    static string ToolStep(SwarmDefinition s, string name)
    {
        var t = s.Tools.FirstOrDefault(x => x.Name == name) ?? throw new SwarmException($"flow stage '{SafeStems.Show(name)}' (Tool) does not exist");
        var who = $"tool '{SafeStems.Show(name)}'";
        var words = new List<string> { "dnx", Arg(t.Package, who, "package") + "@" + Arg(t.Version, who, "version") };
        words.AddRange(t.Args.Select(a => Arg(a, who, "argument")));
        return "Run: " + string.Join(' ', words);
    }

    static string Arg(string value, string who, string what) =>
        SafeArg().IsMatch(value) ? value : throw new SwarmException($"{who}: {what} '{SafeStems.Show(value)}' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)");

    static string Word(string value, string who, string what) =>
        SafeWord().IsMatch(value) ? value : throw new SwarmException($"{who}: {what} '{SafeStems.Show(value)}' is unsafe (allowed characters: letters, digits and _ . -)");

    // ---- scripts ----

    static string RenderScript(SwarmDefinition s, Segment seg)
    {
        var phases = seg.Gates.Select(GatePhase).Concat(seg.Stages.Select(st => st.Target)).ToList();
        var sb = new StringBuilder();
        sb.Append("export const meta = { name: ").Append(Lit($"{s.Name}-{seg.Number}"))
          .Append(", description: ").Append(Lit(s.Description))
          .Append(", phases: [").Append(string.Join(", ", phases.Select(p => $"{{ title: {Lit(p)} }}"))).Append("] }\n\n");
        sb.Append("// Generated from the swarm definition; do not edit.\n");
        sb.Append(seg.Number == 1
            ? "// Input: args.tasks, an array of task descriptions.\n"
            : "// Input: args.state, the array of done results returned by the previous workflow.\n");
        if (seg.Gates.Count > 0) sb.Append("// Gate evidence: args.gates[<gate>] = { green, summary }, produced by the main session.\n");
        sb.Append("// Result: { halted, state, unresolved }; state holds only results with status 'done'.\n");
        sb.Append("const RESULT = ").Append(ResultSchema).Append(";\n\n");

        sb.Append("async function main() {\n");
        sb.Append(seg.Number == 1
            ? "  let state = args && Array.isArray(args.tasks) ? args.tasks : [];\n"
            : "  let state = args && Array.isArray(args.state) ? args.state : [];\n");
        sb.Append("  const unresolved = [];\n");
        sb.Append("  const halt = (reason) => {\n    log(reason);\n    return { halted: true, state: [], unresolved, reason };\n  };\n");

        foreach (var g in seg.Gates.Distinct())
        {
            sb.Append($"\n  phase({Lit(GatePhase(g))});\n");
            sb.Append($"  if (!(args?.gates?.[{Lit(g)}]?.green === true)) return halt({Lit($"gate {g}: evidence is missing or not green")});\n");
        }

        foreach (var st in seg.Stages)
        {
            var role = FindRole(s, st.Target);
            sb.Append("\n  {\n");
            sb.Append($"    phase({Lit(st.Target)});\n");
            if (st.Type == StageType.Fanout) AppendFanout(sb, s, role);
            else AppendRole(sb, role);
            sb.Append("  }\n");
        }

        sb.Append("\n  return { halted: false, state, unresolved };\n}\n\n");
        sb.Append("const result = await main();\nlog(JSON.stringify(result));\nresult;\n");
        return sb.ToString();
    }

    static string GatePhase(string gate) => $"gate {gate}";

    static void AppendFanout(StringBuilder sb, SwarmDefinition s, Role role)
    {
        sb.Append("    const results = await pipeline(\n      state,\n");
        sb.Append($"      (t) => agent(`Task: ${{typeof t === 'string' ? t : JSON.stringify(t)}}`, {AgentOptions(role, withSchema: true)}),\n");
        if (role.EscalateTo is { } to)
        {
            var expert = FindRole(s, to);
            sb.Append("      (res, t) => res && res.status === 'blocked'\n");
            sb.Append("        ? agent(`Task: ${JSON.stringify(t)}. A previous agent could not finish it and returned this result: ${JSON.stringify(res)}. Continue from that hand-off; do not repeat what it already tried.`, ");
            sb.Append(AgentOptions(expert, withSchema: true)).Append(")\n        : res,\n");
        }

        sb.Append("    );\n");
        sb.Append("    const settled = results.map((r, i) => r ?? { status: 'failed', notes: 'agent returned no result', task: state[i] });\n");
        sb.Append("    unresolved.push(...settled.filter((r) => r.status !== 'done'));\n");
        sb.Append("    state = settled.filter((r) => r.status === 'done');\n");
        sb.Append($"    if (state.length === 0) return halt({Lit($"{role.Name}: no task finished with status done")});\n");
    }

    static void AppendRole(StringBuilder sb, Role role)
    {
        sb.Append($"    if (state.length === 0) return halt({Lit($"{role.Name}: there are no completed tasks to work on")});\n");
        sb.Append("    const out = await agent(`You are working on these completed tasks: ${JSON.stringify(state)}. Inspect the work on each listed branch (its diff against the base) and carry out your role on it.`, ");
        sb.Append(AgentOptions(role, withSchema: false)).Append(");\n");
        sb.Append("    log(typeof out === 'string' ? out : JSON.stringify(out));\n");
    }

    static string AgentOptions(Role role, bool withSchema)
    {
        if (string.IsNullOrWhiteSpace(role.Model)) throw new SwarmException($"role '{SafeStems.Show(role.Name)}': model is missing");
        return $"{{ agentType: {Lit(role.Name)}, model: {Lit(role.Model)}, phase: {Lit(role.Name)}{(withSchema ? ", schema: RESULT" : "")} }}";
    }

    static Role FindRole(SwarmDefinition s, string name) =>
        s.Roles.FirstOrDefault(r => r.Name == name && r.Kind == RoleKind.Llm)
        ?? throw new SwarmException($"flow stage '{SafeStems.Show(name)}' does not name an llm role");

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
