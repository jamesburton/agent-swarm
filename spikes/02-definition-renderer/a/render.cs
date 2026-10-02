// THROWAWAY SPIKE 2A: typed canonical model -> renderers. Usage:
//   dotnet run render.cs -- <definition.md|.yaml> <outDir>     render
//   dotnet run render.cs -- --check <outDir>                    parse generated agent files
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

try
{
    if (args is ["--check", var dir]) { Renderers.Check(dir); return 0; }
    if (args.Length != 2) throw new DefinitionException("usage: render.cs <definition.md|.yaml> <outDir> | --check <outDir>");
    var text = File.ReadAllText(args[0]);
    var swarm = Path.GetExtension(args[0]).ToLowerInvariant() switch
    {
        ".md" => MarkdownFrontEnd.Parse(text),
        ".yaml" or ".yml" => YamlFrontEnd.Parse(text),
        var e => throw new DefinitionException($"unsupported definition format '{e}'"),
    };
    swarm.Validate();
    // Render everything in memory first so invalid input can never leave a partial agent set.
    var files = Renderers.RenderAll(swarm);
    foreach (var (rel, content) in files)
    {
        var path = Path.Combine(args[1], rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
    Console.WriteLine($"rendered {files.Count} files for '{swarm.Name}' into {args[1]}");
    return 0;
}
catch (DefinitionException ex)
{
    Console.Error.WriteLine("error: " + ex.Message.ReplaceLineEndings(" "));
    return 1;
}

// ===================== canonical model =====================

enum RoleKind { Code, Llm }

record Role(string Name, RoleKind Kind, string? Model, string Description, string[] Tools, int? MaxTurns,
    string? Effort, string? Isolation, string? EscalateTo, string? Context, string Prompt);

record Gate(string Name, string Kind, string Command);

record Swarm(string Name, string Description, Role[] Roles, Gate[] Gates, string[] Flow)
{
    public static readonly string[] ModelAliases = ["haiku", "sonnet", "opus", "fable", "inherit"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    // Keys a role section / yaml role may carry; anything else is rejected rather than silently dropped.
    public static readonly string[] RoleKeys =
        ["model", "description", "tools", "maxTurns", "effort", "isolation", "escalate-to", "context", "flow", "prompt", "kind", "name"];

    /// <summary>Whole-model validation, shared by every front-end.</summary>
    public void Validate()
    {
        var names = Roles.Select(r => r.Name).Concat(Gates.Select(g => g.Name)).ToList();
        var dup = names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new DefinitionException($"duplicate name '{dup.Key}'");
        var codes = Roles.Where(r => r.Kind == RoleKind.Code).ToList();
        if (codes.Count == 0) throw new DefinitionException("missing section: no '## <name> (code)' orchestrator");
        if (codes.Count > 1) throw new DefinitionException($"extra section: more than one (code) orchestrator ('{codes[1].Name}')");
        if (!Roles.Any(r => r.Kind == RoleKind.Llm)) throw new DefinitionException("missing section: no (llm) role");
        if (Flow.Length == 0) throw new DefinitionException("missing key 'flow' on the orchestrator");
        foreach (var f in Flow.Select(Stage.Parse))
        {
            var ok = f.Type switch
            {
                StageType.Tool => true,
                StageType.Gate => Gates.Any(g => g.Name == f.Target),
                _ => Roles.Any(r => r.Name == f.Target && r.Kind == RoleKind.Llm),
            };
            if (!ok) throw new DefinitionException($"missing section: flow stage '{f.Raw}' has no matching section");
        }
        foreach (var r in Roles.Where(r => r.EscalateTo != null && !Roles.Any(x => x.Name == r.EscalateTo && x.Kind == RoleKind.Llm)))
            throw new DefinitionException($"missing section: '{r.Name}' escalates to unknown role '{r.EscalateTo}'");
    }

    /// <summary>Builds a Role from raw key/value pairs, enforcing required keys and enums.</summary>
    public static Role MakeRole(string name, RoleKind kind, IReadOnlyDictionary<string, string> kv, string body, string swarm)
    {
        foreach (var k in kv.Keys.Where(k => !RoleKeys.Contains(k))) throw new DefinitionException($"unknown key '{k}' in role '{name}'");
        string? Get(string k) => kv.TryGetValue(k, out var v) ? v : null;
        if (kind == RoleKind.Code)
            return new Role(name, kind, null, "", [], null, null, null, null, null, "") { };
        var model = Get("model") ?? throw new DefinitionException($"missing required key 'model' in llm role '{name}'");
        if (!ModelAliases.Contains(model)) throw new DefinitionException($"unknown model alias '{model}' in role '{name}'");
        var effort = Get("effort");
        if (effort != null && !Efforts.Contains(effort)) throw new DefinitionException($"unknown effort '{effort}' in role '{name}'");
        var iso = Get("isolation");
        if (iso != null && iso != "worktree") throw new DefinitionException($"unsupported isolation '{iso}' in role '{name}'");
        int? turns = null;
        if (Get("maxTurns") is { } mt) turns = int.TryParse(mt, out var n) && n > 0 ? n : throw new DefinitionException($"bad maxTurns '{mt}' in role '{name}'");
        var tools = (Get("tools") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new Role(name, kind, model, Get("description") ?? $"{name} role of {swarm}", tools, turns, effort, iso,
            Get("escalate-to"), Get("context"), Get("prompt") ?? body.Trim());
    }
}

enum StageType { Role, Fanout, Gate, Tool }

record Stage(StageType Type, string Target, string Raw)
{
    public static Stage Parse(string raw) =>
        raw.StartsWith("gate:") ? new(StageType.Gate, raw[5..], raw) :
        raw.StartsWith("tool:") ? new(StageType.Tool, raw[5..], raw) :
        raw.EndsWith('*') ? new(StageType.Fanout, raw[..^1], raw) : new(StageType.Role, raw, raw);
}

class DefinitionException(string m) : Exception(m);

// ===================== front-end: Markdown =====================

static class MarkdownFrontEnd
{
    static readonly Regex Role = new(@"^##\s+([A-Za-z][\w-]*)\s*\((code|llm)\)\s*$");
    static readonly Regex GateH = new(@"^##\s+gate\s+([A-Za-z][\w-]*)\s*$");
    static readonly Regex Kv = new(@"^([a-z][A-Za-z-]*):\s*(.*)$");

    public static Swarm Parse(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        var meta = new Dictionary<string, string>();
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            var end = lines.FindIndex(1, l => l.Trim() == "---");
            if (end < 0) throw new DefinitionException("unterminated front-matter");
            foreach (var l in lines[1..end]) if (Kv.Match(l) is { Success: true } m) meta[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            lines = lines[(end + 1)..];
        }
        var name = meta.GetValueOrDefault("name") ?? throw new DefinitionException("missing front-matter key 'name'");
        var desc = meta.GetValueOrDefault("description") ?? "";

        var roles = new List<Role>(); var gates = new List<Gate>(); string[] flow = [];
        string? cur = null; RoleKind kind = default; var isGate = false;
        var kv = new Dictionary<string, string>(); var body = new StringBuilder();

        void Flush()
        {
            if (cur == null) return;
            if (isGate)
            {
                var gk = kv.GetValueOrDefault("kind") ?? throw new DefinitionException($"missing required key 'kind' in gate '{cur}'");
                var gc = kv.GetValueOrDefault("command") ?? throw new DefinitionException($"missing required key 'command' in gate '{cur}'");
                gates.Add(new Gate(cur, gk, gc));
            }
            else
            {
                if (kind == RoleKind.Code && kv.TryGetValue("flow", out var f))
                    flow = f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                roles.Add(Swarm.MakeRole(cur, kind, kv, body.ToString(), name));
            }
            kv = new(); body.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                Flush();
                if (Role.Match(line) is { Success: true } rm) { cur = rm.Groups[1].Value; kind = rm.Groups[2].Value == "code" ? RoleKind.Code : RoleKind.Llm; isGate = false; }
                else if (GateH.Match(line) is { Success: true } gm) { cur = gm.Groups[1].Value; isGate = true; }
                else throw new DefinitionException($"extra section: unrecognised heading '{line.Trim()}' (expected '## name (code|llm)' or '## gate name')");
            }
            else if (cur == null) continue; // title / preamble
            else if (Kv.Match(line) is { Success: true } m)
            {
                if (kv.ContainsKey(m.Groups[1].Value)) throw new DefinitionException($"duplicate key '{m.Groups[1].Value}' in '{cur}'");
                kv[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            else body.AppendLine(line);
        }
        Flush();
        return new Swarm(name, desc, [.. roles], [.. gates], flow);
    }
}

// ===================== front-end: YAML (stub, hand-rolled subset) =====================
// Supports only: top-level scalars, `flow: [a, b]`, and `roles:` / `gates:` lists of `- key: value` maps.
// A real implementation would use YamlDotNet; the point here is the mapping onto the shared model.

static class YamlFrontEnd
{
    public static Swarm Parse(string text)
    {
        var top = new Dictionary<string, string>();
        var items = new Dictionary<string, List<Dictionary<string, string>>> { ["roles"] = [], ["gates"] = [] };
        List<Dictionary<string, string>>? list = null;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#')) continue;
            var indented = raw.StartsWith(' ');
            var s = raw.Trim();
            if (s.StartsWith("- ")) { list?.Add([]); s = s[2..]; }
            var i = s.IndexOf(':');
            if (i < 0) throw new DefinitionException($"yaml: cannot parse line '{s}'");
            var (k, v) = (s[..i].Trim(), s[(i + 1)..].Trim());
            if (!indented) { if (v == "") list = items.GetValueOrDefault(k) ?? throw new DefinitionException($"yaml: unknown section '{k}'"); else top[k] = v; }
            else if (list is { Count: > 0 }) list[^1][k] = v;
            else throw new DefinitionException($"yaml: key '{k}' outside a list item");
        }
        var name = top.GetValueOrDefault("name") ?? throw new DefinitionException("missing key 'name'");
        var flow = top.GetValueOrDefault("flow")?.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var roles = items["roles"].Select(r =>
        {
            var rn = r.GetValueOrDefault("name") ?? throw new DefinitionException("yaml role without name");
            var k = r.GetValueOrDefault("kind") switch { "code" => RoleKind.Code, "llm" => RoleKind.Llm, var x => throw new DefinitionException($"bad kind '{x}' in role '{rn}'") };
            return Swarm.MakeRole(rn, k, r, "", name);
        });
        var gates = items["gates"].Select(g => new Gate(
            g.GetValueOrDefault("name") ?? throw new DefinitionException("yaml gate without name"),
            g.GetValueOrDefault("kind") ?? throw new DefinitionException("missing required key 'kind' in gate"),
            g.GetValueOrDefault("command") ?? throw new DefinitionException("missing required key 'command' in gate")));
        return new Swarm(name, top.GetValueOrDefault("description") ?? "", [.. roles], [.. gates], flow);
    }
}

// ===================== renderers =====================

static class Renderers
{
    static readonly string[] AgentFields = ["name", "description", "model", "tools", "maxTurns", "effort", "isolation"];

    public static Dictionary<string, string> RenderAll(Swarm s)
    {
        var files = new Dictionary<string, string>();
        foreach (var r in s.Roles.Where(r => r.Kind == RoleKind.Llm)) files[$".claude/agents/{r.Name}.md"] = Agent(r);
        files[$".claude/workflows/{s.Name}.js"] = Workflow(s);
        return files;
    }

    static string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Agent(Role r)
    {
        var sb = new StringBuilder("---\n");
        sb.Append($"name: {r.Name}\ndescription: {Q(r.Description)}\nmodel: {r.Model}\n");
        if (r.Tools.Length > 0) sb.Append($"tools: {string.Join(", ", r.Tools)}\n");
        if (r.MaxTurns != null) sb.Append($"maxTurns: {r.MaxTurns}\n");
        if (r.Effort != null) sb.Append($"effort: {r.Effort}\n");
        if (r.Isolation != null) sb.Append($"isolation: {r.Isolation}\n");
        sb.Append("---\n").Append(r.Prompt);
        if (r.Context == "distilled") sb.Append("\n\nYou start with NO prior conversation: everything you know is in the distilled hand-off you were given.");
        return sb.ToString().TrimEnd() + "\n";
    }

    // Double-quoted JS string literal (file-based apps disable reflection JSON, so escape by hand).
    static string J(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    static string Workflow(Swarm s)
    {
        var stages = s.Flow.Select(Stage.Parse).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"export const meta = {{ name: {J(s.Name)}, description: {J(s.Description)}, phases: [{string.Join(", ", stages.Select(st => $"{{ title: {J(st.Raw)} }}"))}] }}");
        sb.AppendLine("// Generated from the swarm definition; do not edit. Pass args.tasks = [\"task text\", ...].");
        sb.AppendLine("const RESULT = { type: 'object', properties: { status: { type: 'string' }, branch: { type: 'string' }, notes: { type: 'string' } }, required: ['status'] }");
        sb.AppendLine("const GATE = { type: 'object', properties: { green: { type: 'boolean' }, summary: { type: 'string' } }, required: ['green'] }");
        sb.AppendLine("let state = args?.tasks ?? []\nlet halted = false\n");
        foreach (var st in stages)
        {
            sb.AppendLine($"phase({J(st.Raw)})");
            sb.AppendLine("if (!halted) {");
            switch (st.Type)
            {
                case StageType.Fanout:
                {
                    var r = s.Roles.First(x => x.Name == st.Target);
                    var opts = $"{{ agentType: {J(r.Name)}, model: {J(r.Model!)}, phase: {J(st.Raw)}, schema: RESULT }}";
                    sb.AppendLine($"  const run = (t) => agent(`Task: ${{typeof t === 'string' ? t : JSON.stringify(t)}}`, {opts})");
                    if (r.EscalateTo is { } e)
                    {
                        var x = s.Roles.First(y => y.Name == e);
                        sb.AppendLine($"  const escalate = (res, t) => res && res.status === 'blocked' ? agent(`Distilled hand-off for the expert. Task: ${{JSON.stringify(t)}}. Worker result: ${{JSON.stringify(res)}}`, {{ agentType: {J(x.Name)}, model: {J(x.Model!)}, phase: {J(st.Raw)}, schema: RESULT }}) : res");
                        sb.AppendLine("  state = (await pipeline(state, run, escalate)).filter(Boolean)");
                    }
                    else sb.AppendLine("  state = (await pipeline(state, run)).filter(Boolean)");
                    break;
                }
                case StageType.Role:
                {
                    var r = s.Roles.First(x => x.Name == st.Target);
                    sb.AppendLine($"  log(String(await agent(`Process: ${{JSON.stringify(state)}}`, {{ agentType: {J(r.Name)}, model: {J(r.Model!)}, phase: {J(st.Raw)} }})))");
                    break;
                }
                case StageType.Gate:
                {
                    var g = s.Gates.First(x => x.Name == st.Target);
                    sb.AppendLine($"  const gate = await agent({J($"Run this command with the Bash tool and report whether it exited 0: {g.Command}")}, {{ model: 'haiku', effort: 'low', phase: {J(st.Raw)}, schema: GATE }})");
                    sb.AppendLine($"  if (!gate || !gate.green) {{ halted = true; log({J($"gate {g.Name} ({g.Kind}) red")}) }}");
                    break;
                }
                case StageType.Tool:
                    // Tool step: delegated to a dnx-style CLI via a cheap Bash-running agent.
                    sb.AppendLine($"  await agent(`Run with the Bash tool: dnx {st.Target} --yes -- (branches: ${{JSON.stringify(state)}}). Report the exit code.`, {{ model: 'haiku', effort: 'low', phase: {J(st.Raw)} }})");
                    break;
            }
            sb.AppendLine("}\n");
        }
        sb.AppendLine("log(JSON.stringify({ halted, results: state }))");
        return sb.ToString();
    }

    /// <summary>Parses each generated agent file's front-matter and verifies fields are in the supported list.</summary>
    public static void Check(string outDir)
    {
        var files = Directory.GetFiles(Path.Combine(outDir, ".claude", "agents"), "*.md");
        if (files.Length == 0) throw new DefinitionException("no agent files");
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            if (lines.Length < 3 || lines[0] != "---") throw new DefinitionException($"{f}: no front-matter");
            var end = Array.IndexOf(lines, "---", 1);
            if (end < 0) throw new DefinitionException($"{f}: unterminated front-matter");
            var kv = lines[1..end].Select(l => l.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim());
            foreach (var k in kv.Keys.Where(k => !AgentFields.Contains(k))) throw new DefinitionException($"{f}: unsupported field '{k}'");
            foreach (var req in new[] { "name", "description", "model" }) if (!kv.ContainsKey(req)) throw new DefinitionException($"{f}: missing '{req}'");
            if (kv["name"] != Path.GetFileNameWithoutExtension(f)) throw new DefinitionException($"{f}: name != filename");
            if (!Swarm.ModelAliases.Contains(kv["model"])) throw new DefinitionException($"{f}: bad model");
            Console.WriteLine($"ok {Path.GetFileName(f)} fields=[{string.Join(",", kv.Keys)}]");
        }
    }
}
