using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Swarm.Core;

namespace Swarm.Render;

/// <summary>Renders the LLM roles of a swarm as Claude Code sub-agent files.</summary>
public static partial class AgentFileRenderer
{
    const string DistilledNote = "You start with NO prior conversation: everything you know is in the distilled hand-off you were given.";

    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeStem();

    /// <summary>Renders one agent file per LLM role (the code orchestrator is excluded).</summary>
    /// <param name="s">The swarm definition.</param>
    /// <returns>Relative path (<c>.claude/agents/&lt;role&gt;.md</c>) to file content, enumerated in role declaration order.</returns>
    /// <exception cref="SwarmException">Thrown when a role name is not a safe file stem, two names collide case-insensitively, or a field holds disallowed control characters.</exception>
    public static IReadOnlyDictionary<string, string> Render(SwarmDefinition s)
    {
        var roles = s.Roles.Where(r => r.Kind == RoleKind.Llm).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roles)
        {
            if (!SafeStem().IsMatch(r.Name) || ReservedDeviceNames.Contains(r.Name))
                throw new SwarmException($"role '{r.Name}': name is not a safe file name (use letters, digits, '_' or '-'; not a reserved device name)");
            if (!seen.Add(r.Name))
                throw new SwarmException($"role '{r.Name}': name collides case-insensitively with another role");
        }

        var files = new OrderedFiles();
        foreach (var r in roles) files.Add($".claude/agents/{r.Name}.md", RenderRole(r));
        return files;
    }

    static string RenderRole(Role r)
    {
        // The front-matter is written and closed before the prompt, so a '---' line in the prompt cannot affect it.
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(Plain(r.Name, "name")).Append('\n');
        sb.Append("description: ").Append(Quote(r.Description, r.Name)).Append('\n');
        sb.Append("model: ").Append(Plain(r.Model ?? "", "model")).Append('\n');
        sb.Append("tools: ").Append(string.Join(", ", r.Tools.Select(t => Plain(t, "tools")))).Append('\n');
        if (r.MaxTurns is { } m) sb.Append("maxTurns: ").Append(m).Append('\n');
        if (r.Effort is { } e) sb.Append("effort: ").Append(Plain(e, "effort")).Append('\n');
        if (r.Isolation is { } i) sb.Append("isolation: ").Append(Plain(i, "isolation")).Append('\n');
        sb.Append("---\n");

        sb.Append(Prompt(r.Prompt, r.Name).TrimEnd('\n')).Append('\n');
        if (r.Context == "distilled") sb.Append(DistilledNote).Append('\n');
        return sb.ToString();
    }

    // Unquoted front-matter scalar: must be a single line with no control characters.
    static string Plain(string value, string field)
    {
        if (value.Any(char.IsControl)) throw new SwarmException($"field '{field}' contains a control character");
        return value;
    }

    // Always double-quoted; backslash, quote and common whitespace controls are escaped, other controls rejected.
    static string Quote(string value, string role)
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
                    if (char.IsControl(c)) throw new SwarmException($"role '{role}': description contains a control character");
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    // Normalises line endings to LF; tab is allowed, other control characters are rejected.
    static string Prompt(string value, string role)
    {
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
            throw new SwarmException($"role '{role}': prompt contains a control character");
        return text;
    }

    // Insertion-ordered read-only map (a plain Dictionary does not guarantee enumeration order).
    sealed class OrderedFiles : IReadOnlyDictionary<string, string>
    {
        readonly List<KeyValuePair<string, string>> items = [];

        public void Add(string key, string value) => items.Add(new(key, value));

        public string this[string key] => items.First(p => p.Key == key).Value;

        public IEnumerable<string> Keys => items.Select(p => p.Key);

        public IEnumerable<string> Values => items.Select(p => p.Value);

        public int Count => items.Count;

        public bool ContainsKey(string key) => items.Any(p => p.Key == key);

        public bool TryGetValue(string key, out string value)
        {
            foreach (var p in items)
            {
                if (p.Key == key) { value = p.Value; return true; }
            }

            value = "";
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
