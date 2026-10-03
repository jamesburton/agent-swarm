using Swarm.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Swarm.Formats;

/// <summary>Parses the YAML swarm definition format into a validated <see cref="SwarmDefinition"/>.</summary>
public static class YamlFrontEnd
{
    static readonly HashSet<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

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
        var text = yaml.TrimStart('﻿').ReplaceLineEndings("\n");
        SwarmDto? dto;
        try
        {
            RejectDuplicateKeys(text);
            dto = new DeserializerBuilder().WithNamingConvention(Naming.Instance).Build().Deserialize<SwarmDto?>(text);
        }
        catch (YamlException ex) { throw OneLine(ex); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new SwarmException(Collapse($"invalid YAML: {ex.Message}")); }
        if (dto == null) throw new SwarmException("empty or non-mapping YAML document");
        return Map(dto);
    }

    static void RejectDuplicateKeys(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        foreach (var doc in stream.Documents) Walk(doc.RootNode);

        static void Walk(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode map:
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (k, v) in map.Children)
                    {
                        if (k is YamlScalarNode s && !seen.Add(s.Value ?? ""))
                            throw new YamlException(k.Start, k.End, $"duplicate key '{s.Value}'");
                        Walk(v);
                    }
                    break;
                case YamlSequenceNode seq:
                    foreach (var c in seq.Children) Walk(c);
                    break;
            }
        }
    }

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
        if (r.Effort != null && !Efforts.Contains(r.Effort)) throw new SwarmException($"unknown effort '{r.Effort}' in role '{name}'");
        if (r.Isolation != null && r.Isolation != "worktree") throw new SwarmException($"unsupported isolation '{r.Isolation}' in role '{name}'");
        int? turns = null;
        if (r.MaxTurns is { } mt)
            turns = int.TryParse(mt, out var n) && n > 0 ? n : throw new SwarmException($"bad maxTurns '{mt}' in role '{name}'");
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
