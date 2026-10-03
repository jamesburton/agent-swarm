using System.Text;
using System.Text.RegularExpressions;
using Swarm.Core;

namespace Swarm.Formats;

/// <summary>Parses the Markdown swarm definition format into a validated <see cref="SwarmDefinition"/>.</summary>
public static class MarkdownFrontEnd
{
    static readonly Regex RoleHeading = new(@"^##\s+([A-Za-z][\w-]*)\s*\((code|llm)\)\s*$");
    static readonly Regex ToolHeading = new(@"^##\s+tool:\s*([A-Za-z][\w-]*)\s*$");
    static readonly Regex GateHeading = new(@"^##\s+gate:\s*([A-Za-z][\w-]*)\s*$");
    static readonly Regex KeyValue = new(@"^([A-Za-z][\w-]*):\s*(.*)$");

    static readonly HashSet<string> FrontMatterKeys = ["name", "description"];
    static readonly HashSet<string> RoleKeys = ["model", "description", "tools", "maxTurns", "effort", "isolation", "escalate-to", "context"];
    static readonly Regex AnyHeading = new(@"^##(\s|$)");
    static readonly HashSet<string> ToolKeys = ["package", "version", "args"];
    static readonly HashSet<string> GateKeys = ["kind", "tool"];
    static readonly HashSet<string> CodeKeys = ["flow"];

    enum SectionKind { Code, Llm, Tool, Gate }

    /// <summary>Parses and validates a Markdown definition.</summary>
    /// <param name="text">The Markdown text (LF or CRLF, optional BOM).</param>
    /// <returns>The validated definition.</returns>
    /// <exception cref="SwarmException">Thrown when the text is malformed or invalid.</exception>
    public static SwarmDefinition Parse(string text)
    {
        var lines = text.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n').ToList();
        var meta = new Dictionary<string, string>();
        if (lines[0].Trim() == "---")
        {
            var end = lines.FindIndex(1, l => l.Trim() == "---");
            if (end < 0) throw new SwarmException("unterminated front-matter");
            foreach (var l in lines[1..end])
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (KeyValue.Match(l) is not { Success: true } m) throw new SwarmException($"unexpected text in front-matter: '{SafeText.Show(l.Trim())}'");
                var key = m.Groups[1].Value;
                if (!FrontMatterKeys.Contains(key)) throw new SwarmException($"unknown front-matter key '{key}'");
                if (!meta.TryAdd(key, m.Groups[2].Value.Trim())) throw new SwarmException($"duplicate front-matter key '{key}'");
            }
            lines = lines[(end + 1)..];
        }

        var name = meta.GetValueOrDefault("name") ?? throw new SwarmException("missing front-matter key 'name'");
        var roles = new List<Role>();
        var tools = new List<ToolDef>();
        var gates = new List<Gate>();
        IReadOnlyList<Stage> flow = [];

        string? cur = null;
        var kind = SectionKind.Code;
        var kv = new Dictionary<string, string>();
        var body = new StringBuilder();

        void Flush()
        {
            if (cur == null) return;
            switch (kind)
            {
                case SectionKind.Tool:
                    tools.Add(new ToolDef(cur, kv.GetValueOrDefault("package") ?? "", kv.GetValueOrDefault("version") ?? "", List(kv.GetValueOrDefault("args"))));
                    break;
                case SectionKind.Gate:
                    gates.Add(new Gate(cur, kv.GetValueOrDefault("kind") ?? throw new SwarmException($"missing required key 'kind' in gate '{cur}'"), kv.GetValueOrDefault("tool")));
                    break;
                case SectionKind.Code:
                    if (kv.TryGetValue("flow", out var f)) flow = FlowParser.Parse(List(f));
                    roles.Add(new Role(cur, RoleKind.Code, null, "", [], null, null, null, null, null, ""));
                    break;
                default:
                    roles.Add(MakeLlmRole(cur, kv, body.ToString(), name));
                    break;
            }
            kv = new();
            body.Clear();
        }

        foreach (var line in lines)
        {
            if (AnyHeading.IsMatch(line))
            {
                Flush();
                if (RoleHeading.Match(line) is { Success: true } rm) (cur, kind) = (rm.Groups[1].Value, rm.Groups[2].Value == "code" ? SectionKind.Code : SectionKind.Llm);
                else if (ToolHeading.Match(line) is { Success: true } tm) (cur, kind) = (tm.Groups[1].Value, SectionKind.Tool);
                else if (GateHeading.Match(line) is { Success: true } gm) (cur, kind) = (gm.Groups[1].Value, SectionKind.Gate);
                else throw new SwarmException($"unrecognised heading '{line.Trim()}' (expected '## name (code|llm)', '## tool: name' or '## gate: name')");
            }
            else if (cur == null || (body.Length == 0 && string.IsNullOrWhiteSpace(line))) continue; // title / preamble / blank lines before the prompt
            else if (body.Length == 0 && KeyValue.Match(line) is { Success: true } m)
            {
                // Keys only count before the prompt body starts (llm roles keep later "key: value" text in the prompt).
                var allowed = kind switch { SectionKind.Tool => ToolKeys, SectionKind.Gate => GateKeys, SectionKind.Code => CodeKeys, _ => null };
                if (allowed != null && !allowed.Contains(m.Groups[1].Value))
                    throw new SwarmException($"unknown key '{m.Groups[1].Value}' in {kind.ToString().ToLowerInvariant()} '{cur}'");
                if (!kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim()))
                    throw new SwarmException($"duplicate key '{m.Groups[1].Value}' in '{cur}'");
            }
            else if (kind != SectionKind.Llm) throw new SwarmException($"unexpected text in {kind.ToString().ToLowerInvariant()} '{cur}': '{line.Trim()}'");
            else body.Append(line).Append('\n');
        }
        Flush();
        return Validator.Validated(new SwarmDefinition(name, meta.GetValueOrDefault("description") ?? "", roles, tools, gates, flow));
    }

    static string[] List(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        var items = v.Split(',', StringSplitOptions.TrimEntries);
        if (items.Any(i => i.Length == 0)) throw new SwarmException($"empty entry in list '{v}'");
        return items;
    }

    static Role MakeLlmRole(string name, Dictionary<string, string> kv, string body, string swarm)
    {
        foreach (var k in kv.Keys.Where(k => !RoleKeys.Contains(k))) throw new SwarmException($"unknown key '{k}' in role '{name}'");
        string? Get(string k) => kv.GetValueOrDefault(k);
        var effort = Get("effort");
        var isolation = Get("isolation");
        var turns = RoleFields.Check(name, effort, isolation, Get("maxTurns"), Get("context"));
        return new Role(name, RoleKind.Llm, Get("model"), Get("description") ?? $"{name} role of {swarm}", List(Get("tools")),
            turns, effort, isolation, Get("escalate-to"), Get("context"), body.Trim());
    }
}
