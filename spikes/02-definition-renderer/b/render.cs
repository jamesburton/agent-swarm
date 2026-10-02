// THROWAWAY SPIKE 2B: thin pass-through renderer. No typed model: sections stay dictionaries.
// usage: dotnet run render.cs -- <definition.md> <outDir>
using System.Text.RegularExpressions;

// Documented sub-agent front-matter (docs/capabilities.md s1) + name/description; swarm-only keys stay out of agent files.
string[] Allowed = { "name", "description", "model", "tools", "disallowedTools", "skills", "mcpServers", "permissionMode",
    "maxTurns", "effort", "background", "memory", "color", "omitClaudeMd", "initialPrompt", "isolation", "hooks" };
string[] SwarmOnly = { "context", "escalate-to" };
string[] Aliases = { "haiku", "sonnet", "opus", "fable", "inherit" };

try
{
    if (args.Length != 2) throw new Exception("usage: render <definition.md> <outDir>");
    var outDir = args[1];
    var files = new Dictionary<string, string>(); // all output built in memory first => never a partial emit
    var secs = Parse(File.ReadAllLines(args[0]));
    var epic = secs.FirstOrDefault(s => s.Title.StartsWith("# "))?.Title[2..].Trim() ?? "workflow";
    secs = secs.Where(s => !s.Title.StartsWith("# ")).ToList();

    var orch = secs.Where(s => s.Kind == "code").ToList();
    if (orch.Count != 1) throw new Exception($"expected exactly one '## <name>  (code)' orchestrator section, found {orch.Count}");
    var stages = (orch[0].Keys.GetValueOrDefault("stages") ?? throw new Exception("orchestrator is missing required key 'stages'"))
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (stages.Length == 0) throw new Exception("orchestrator 'stages' is empty");

    var names = new HashSet<string>();
    foreach (var s in secs.Where(s => s.Kind != "code"))
        if (!names.Add(s.Name)) throw new Exception($"duplicate section name '{s.Name}'");

    foreach (var r in secs.Where(s => s.Kind != "code" && s.Kind != "gate"))
    {
        if (r.Kind == "") throw new Exception($"role '{r.Name}' is missing required model: use '## {r.Name}  (<model>)'");
        if (!Aliases.Contains(r.Kind) && !Regex.IsMatch(r.Kind, @"^claude-[\w.\-]+$"))
            throw new Exception($"role '{r.Name}': unknown model alias '{r.Kind}' (allowed: {string.Join(", ", Aliases)} or claude-<id>)");
        foreach (var k in r.Keys.Keys)
            if (!Allowed.Contains(k) && !SwarmOnly.Contains(k)) throw new Exception($"role '{r.Name}': unknown front-matter key '{k}'");
        if (!r.Keys.ContainsKey("description")) throw new Exception($"role '{r.Name}' is missing required key 'description'");
        var fm = new List<string> { $"name: {r.Name}", $"model: {r.Kind}" };
        fm.AddRange(r.Keys.Where(kv => Allowed.Contains(kv.Key) && kv.Key != "name").Select(kv => $"{kv.Key}: {kv.Value}"));
        files[$".claude/agents/{r.Name}.md"] = $"---\n{string.Join("\n", fm)}\n---\n\n{r.Body}\n";
    }
    foreach (var g in secs.Where(s => s.Kind == "gate"))
        if (!g.Keys.ContainsKey("kind")) throw new Exception($"gate '{g.Name}' is missing required key 'kind'");
    foreach (var st in stages)
        if (!names.Contains(st)) throw new Exception($"stage '{st}' matches no role or gate section");

    // Workflow template: meta is a pure literal; stages become a data array driven by one generic runner.
    string Q(string s) => "'" + s.Replace("'", "\\'") + "'";
    var stageJs = string.Join(",\n  ", stages.Select(st =>
    {
        var s = secs.First(x => x.Name == st);
        return s.Kind == "gate"
            ? $"{{ name: {Q(st)}, gate: true, kind: {Q(s.Keys["kind"])}, command: {Q(s.Keys.GetValueOrDefault("command") ?? "")} }}"
            : $"{{ name: {Q(st)}, gate: false, agentType: {Q(st)} }}";
    }));
    var phases = string.Join(",\n    ", stages.Select(st =>
        $"{{ title: {Q(st)}, detail: {Q(secs.First(x => x.Name == st).Kind == "gate" ? "gate" : "agent")} }}"));
    var desc = Q("Generated from " + Path.GetFileName(args[0]) + ": " + string.Join(" -> ", stages));
    files[$".claude/workflows/{epic}.js"] = $$"""
export const meta = {
  name: {{Q(epic)}},
  description: {{desc}},
  phases: [
    {{phases}}
  ],
}

// args: { tickets: [ { id, title } ] }. Each ticket flows through every stage independently.
const STAGES = [
  {{stageJs}}
]

const results = await pipeline(args.tickets, ...STAGES.map(st => (prev, ticket) =>
  st.gate
    ? agent(`Run the ${st.kind} gate '${st.name}' for ticket ${ticket.id} with: ${st.command}. Report pass/fail with evidence.`,
        { label: `${st.name}:${ticket.id}`, phase: st.name, model: 'haiku' })
    : agent(`Ticket ${ticket.id}: ${ticket.title}\nPrevious stage result:\n${JSON.stringify(prev)}`,
        { label: `${st.name}:${ticket.id}`, phase: st.name, agentType: st.agentType })))
return results

""";

    foreach (var (rel, text) in files)
    {
        var p = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }
    Console.WriteLine($"rendered {files.Count} files to {outDir}");
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message.ReplaceLineEndings(" "));
    return 1;
}
return 0;

// Heading '## name  (kind)' or '## gate name'; '# title' kept as a pseudo-section. Leading 'key: value' lines are front matter.
static List<Sec> Parse(string[] lines)
{
    var list = new List<Sec>(); Sec? cur = null;
    foreach (var l in lines)
    {
        if (l.StartsWith("# ")) { list.Add(new Sec { Title = l }); cur = null; continue; }
        if (l.StartsWith("## "))
        {
            var t = l[3..].Trim(); var m = Regex.Match(t, @"^([\w\-]+)\s*\(([\w.\-]+)\)$"); var g = Regex.Match(t, @"^gate\s+([\w\-]+)$");
            cur = m.Success ? new Sec { Title = l, Name = m.Groups[1].Value, Kind = m.Groups[2].Value }
                : g.Success ? new Sec { Title = l, Name = g.Groups[1].Value, Kind = "gate" }
                : Regex.IsMatch(t, @"^[\w\-]+$") ? new Sec { Title = l, Name = t, Kind = "" }
                : throw new Exception($"unrecognised section heading '{l}'");
            list.Add(cur); continue;
        }
        if (cur == null) continue;
        var kv = Regex.Match(l, @"^([\w\-]+):\s*(.+)$");
        if (!cur.InBody && kv.Success) cur.Keys[kv.Groups[1].Value] = kv.Groups[2].Value.Trim();
        else if (cur.InBody || l.Trim() != "") { cur.InBody = true; cur.Lines.Add(l); }
    }
    return list;
}

class Sec
{
    public string Title = "", Name = "", Kind = ""; public bool InBody;
    public Dictionary<string, string> Keys = new(); public List<string> Lines = new();
    public string Body => string.Join("\n", Lines).Trim();
}
