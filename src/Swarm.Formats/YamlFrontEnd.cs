using Swarm.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Swarm.Formats;

/// <summary>Parses the YAML swarm definition format into a validated <see cref="SwarmDefinition"/>.</summary>
public static class YamlFrontEnd
{
    /// <summary>Hyphenated keys (<c>escalate-to</c>) except the camel-case <c>maxTurns</c> shared with the Markdown format.</summary>
    sealed class Naming : INamingConvention
    {
        public static readonly Naming Instance = new();

        public string Apply(string value) => value == "MaxTurns" ? "maxTurns" : HyphenatedNamingConvention.Instance.Apply(value);

        public string Reverse(string value) => HyphenatedNamingConvention.Instance.Reverse(value);
    }

    sealed class SwarmDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, RoleDto?>? Roles { get; set; }
        public Dictionary<string, ToolDto?>? Tools { get; set; }
        public Dictionary<string, GateDto?>? Gates { get; set; }
    }

    sealed class RoleDto
    {
        public string? Kind { get; set; }
        public string? Model { get; set; }
        public string? Description { get; set; }
        public List<string?>? Tools { get; set; }
        public string? MaxTurns { get; set; }
        public string? Effort { get; set; }
        public string? Isolation { get; set; }
        public string? EscalateTo { get; set; }
        public string? Context { get; set; }
        public string? Prompt { get; set; }
        public List<string?>? Flow { get; set; }
    }

    sealed class ToolDto
    {
        public string? Package { get; set; }
        public string? Version { get; set; }
        public List<string?>? Args { get; set; }
    }

    sealed class GateDto
    {
        public string? Kind { get; set; }
        public string? Tool { get; set; }
    }

    /// <summary>Parses and validates a YAML definition.</summary>
    /// <param name="yaml">The YAML text (LF or CRLF, optional BOM).</param>
    /// <returns>The validated definition.</returns>
    /// <exception cref="SwarmException">Thrown when the text is malformed or invalid; the message is always a single line.</exception>
    public static SwarmDefinition Parse(string yaml)
    {
        var text = yaml.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        SwarmDto? dto;
        try
        {
            CheckStructure(text);
            dto = new DeserializerBuilder().WithNamingConvention(Naming.Instance).Build().Deserialize<SwarmDto?>(text);
        }
        catch (YamlException ex) { throw OneLine(ex); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new SwarmException(Collapse($"invalid YAML: {ex.Message}")); }
        if (dto == null) throw new SwarmException("empty or non-mapping YAML document");
        return Map(dto);
    }

    // Keys each mapping accepts, derived from the DTOs so the lists cannot drift from what the deserializer binds.
    static readonly HashSet<string> TopKeys = Keys<SwarmDto>(), RoleKeys = Keys<RoleDto>(), ToolKeys = Keys<ToolDto>(), GateKeys = Keys<GateDto>();

    static HashSet<string> Keys<T>() => typeof(T).GetProperties().Select(p => Naming.Instance.Apply(p.Name)).ToHashSet(StringComparer.Ordinal);

    // Rejects anchors, aliases, merge keys, duplicate keys and empty values, then unknown keys (named with their role, tool or gate).
    static void CheckStructure(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        foreach (var doc in stream.Documents) Walk(doc.RootNode);
        if (stream.Documents.FirstOrDefault()?.RootNode is YamlMappingNode root) RejectUnknownKeys(root);

        static void Walk(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode map:
                    RejectAnchor(map);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (k, v) in map.Children)
                    {
                        if (k is YamlScalarNode { Value: "<<" }) throw Unsupported(k);
                        RejectAnchor(k);
                        RejectAnchor(v);
                        // Deliberate: an empty/null value (e.g. `prompt:`, `description:`, `tools:`, `args:`) is rejected; `[]` and `""` remain allowed.
                        if (k is YamlScalarNode key && v is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" or "Null" or "NULL" })
                            throw new YamlException(k.Start, k.End, $"key '{key.Value}' has no value");
                        if (k is YamlScalarNode s && !seen.Add(s.Value ?? ""))
                            throw new YamlException(k.Start, k.End, $"duplicate key '{s.Value}'");
                        Walk(v);
                    }
                    break;
                case YamlSequenceNode seq:
                    RejectAnchor(seq);
                    foreach (var c in seq.Children)
                    {
                        RejectAnchor(c);
                        Walk(c);
                    }
                    break;
            }
        }
    }

    static void RejectUnknownKeys(YamlMappingNode root)
    {
        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").Where(k => !TopKeys.Contains(k)))
            throw new SwarmException($"unknown top-level key '{SafeText.Show(key)}'");
        Section("roles", "role", RoleKeys);
        Section("tools", "tool", ToolKeys);
        Section("gates", "gate", GateKeys);

        void Section(string key, string noun, HashSet<string> allowed)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlMappingNode entries) return;
            foreach (var (name, body) in entries.Children)
            {
                if (name is not YamlScalarNode { Value: var owner } || body is not YamlMappingNode fields) continue;
                foreach (var k in fields.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").Where(k => !allowed.Contains(k)))
                    throw new SwarmException($"unknown key '{SafeText.Show(k)}' in {noun} '{SafeText.Show(owner ?? "")}'");
            }
        }
    }

    static void RejectAnchor(YamlNode n)
    {
        if (!n.Anchor.IsEmpty) throw Unsupported(n);
    }

    static YamlException Unsupported(YamlNode n) =>
        new(n.Start, n.End, "YAML anchors/aliases/merge keys are not supported; write the content explicitly");

    static SwarmException OneLine(YamlException ex)
    {
        // Prefer the innermost message (e.g. "Property 'x' not found") over the generic wrapper.
        var inner = ex;
        while (inner.InnerException is YamlException i) inner = i;
        var line = ex.Start.Line > 0 ? $" (line {ex.Start.Line})" : "";
        return new SwarmException(Collapse($"invalid YAML{line}: {inner.Message}"));
    }

    static string Collapse(string s) => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    static SwarmDefinition Map(SwarmDto d)
    {
        var name = string.IsNullOrWhiteSpace(d.Name) ? throw new SwarmException("missing key 'name'") : d.Name.Trim();
        IReadOnlyList<Stage> flow = [];
        var roles = new List<Role>();
        foreach (var (rn, r) in d.Roles ?? [])
        {
            var role = r ?? throw new SwarmException($"role '{rn}' is empty");
            if (role.Kind == "code")
            {
                var extra = new (string Key, bool Set)[]
                {
                    ("model", role.Model != null), ("description", role.Description != null), ("tools", role.Tools != null),
                    ("maxTurns", role.MaxTurns != null), ("effort", role.Effort != null), ("isolation", role.Isolation != null),
                    ("escalate-to", role.EscalateTo != null), ("context", role.Context != null), ("prompt", role.Prompt != null),
                }.FirstOrDefault(x => x.Set);
                if (extra.Key != null) throw new SwarmException($"unknown key '{extra.Key}' in code role '{rn}'");
                if (role.Flow != null) flow = FlowParser.Parse(Items(role.Flow, $"role '{rn}' flow"));
                roles.Add(new Role(rn, RoleKind.Code, null, "", [], null, null, null, null, null, ""));
            }
            else if (role.Kind == "llm") roles.Add(MakeLlmRole(rn, role, name));
            else throw new SwarmException(role.Kind == null ? $"missing required key 'kind' in role '{rn}'" : $"unknown kind '{role.Kind}' in role '{rn}' (expected code or llm)");
        }

        var tools = (d.Tools ?? []).Select(t =>
        {
            var tool = t.Value ?? throw new SwarmException($"tool '{t.Key}' is empty");
            return new ToolDef(t.Key, tool.Package ?? "", tool.Version ?? "", Items(tool.Args, $"tool '{t.Key}' args"));
        }).ToList();
        var gates = (d.Gates ?? []).Select(g =>
            new Gate(g.Key, g.Value?.Kind ?? throw new SwarmException($"missing required key 'kind' in gate '{g.Key}'"), g.Value.Tool)).ToList();
        return Validator.Validated(new SwarmDefinition(name, d.Description ?? "", roles, tools, gates, flow));
    }

    static Role MakeLlmRole(string name, RoleDto r, string swarm)
    {
        if (r.Flow != null) throw new SwarmException($"unknown key 'flow' in llm role '{name}'");
        var turns = RoleFields.Check(name, r.Effort, r.Isolation, r.MaxTurns, r.Context);
        return new Role(name, RoleKind.Llm, r.Model, r.Description ?? $"{name} role of {swarm}", Items(r.Tools, $"role '{name}' tools"),
            turns, r.Effort, r.Isolation, r.EscalateTo, r.Context, (r.Prompt ?? "").Trim());
    }

    static string[] Items(List<string?>? list, string where)
    {
        if (list == null) return [];
        var items = list.Select(i => i?.Trim() ?? "").ToArray();
        if (items.Any(i => i.Length == 0)) throw new SwarmException($"empty entry in {where}");
        return items;
    }
}
