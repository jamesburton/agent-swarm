using System.Text.RegularExpressions;
namespace Swarm.Core;

/// <summary>Thrown when a swarm definition is invalid.</summary>
/// <param name="message">The error message.</param>
public class SwarmException(string message) : Exception(message);

/// <summary>Validates a <see cref="SwarmDefinition"/>.</summary>
public static class Validator
{
    static readonly HashSet<string> Aliases = ["haiku", "sonnet", "opus", "fable", "inherit"];

    // \z (not $) so a trailing newline cannot slip through.
    static readonly Regex Pinned = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\z");

    // NuGet-style package id: letters, digits, '_', '.', '-', starting with a letter or digit (so never an option such as '-x').
    static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z");

    // Words a YAML 1.1/1.2 reader turns into null or a boolean; a role name is written unquoted as the agent file's `name:`.
    static readonly HashSet<string> YamlWords = new(StringComparer.OrdinalIgnoreCase) { "null", "true", "false", "yes", "no", "on", "off", "y", "n", "~" };

    // Plain scalars a YAML reader turns into a number (decimal with optional exponent, hex, octal, binary; '_' digit separators).
    // Timestamps a YAML 1.1 reader (js-yaml's default schema) turns into a date: yyyy-m-d, optionally followed by a time.
    static readonly Regex YamlDate = new(@"^\d{4}-\d{1,2}-\d{1,2}([Tt ].*)?\z");

    static readonly Regex YamlNumber = new(@"^[-+]?(0x[0-9a-f_]+|0o[0-7_]+|0b[01_]+|[0-9][0-9_]*(e[-+]?[0-9]+)?)\z", RegexOptions.IgnoreCase);

    /// <summary>True when the version is an exact pinned version such as <c>1.2.3</c> or <c>1.2.3-rc.1</c>.</summary>
    /// <param name="version">The version text.</param>
    /// <returns>Whether it is pinned.</returns>
    public static bool IsPinnedVersion(string? version) => Pinned.IsMatch(version ?? "");

    /// <summary>Returns all validation errors (empty when valid).</summary>
    /// <param name="s">The definition to check.</param>
    /// <returns>The error messages.</returns>
    public static IReadOnlyList<string> Check(SwarmDefinition s)
    {
        var errors = new List<string>();
        static string Show(string? v) => SafeText.Show(v ?? "");

        if (string.IsNullOrWhiteSpace(s.Name)) errors.Add("swarm name must not be empty");
        var named = s.Roles.Select(r => ("role", r.Name)).Concat(s.Tools.Select(t => ("tool", t.Name))).Concat(s.Gates.Select(g => ("gate", g.Name))).ToList();
        foreach (var (kind, _) in named.Where(n => string.IsNullOrWhiteSpace(n.Name)).DistinctBy(n => n.Item1))
            errors.Add($"{kind} name must not be empty");

        // Names are compared case-insensitively: agent and workflow files are named after them, and Windows and macOS file systems ignore case.
        foreach (var group in named.Select(n => n.Name).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var spellings = group.Distinct(StringComparer.Ordinal).ToList();
            errors.Add(spellings.Count < group.Count()
                ? $"duplicate name '{Show(group.First(n => group.Count(x => x == n) > 1))}'"
                : $"names '{Show(spellings[0])}' and '{Show(spellings[1])}' differ only by case");
        }

        if (s.Roles.Count(r => r.Kind == RoleKind.Code) != 1)
            errors.Add("expected exactly one code orchestrator role");
        if (s.Flow.Count == 0)
            errors.Add("flow is empty (the orchestrator needs at least one stage)");

        var used = s.Flow.Where(f => f.Type is StageType.Fanout or StageType.Role).Select(f => f.Target)
            .Concat(s.Roles.Select(r => r.EscalateTo).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var r in s.Roles.Where(r => r.Kind == RoleKind.Llm))
        {
            if (YamlWords.Contains(r.Name) || YamlNumber.IsMatch(r.Name) || YamlDate.IsMatch(r.Name))
                errors.Add($"role '{Show(r.Name)}': name reads as a YAML value (null, true, false, yes, no, on, off, y, n, ~, a number or a date); choose another name");
            if (!r.Name.Any(char.IsLetterOrDigit))
                errors.Add($"role '{Show(r.Name)}': name must contain a letter or a digit");
            if (string.IsNullOrWhiteSpace(r.Model)) errors.Add($"role '{Show(r.Name)}': missing required model");
            else if (!Aliases.Contains(r.Model) && !r.Model.StartsWith("claude-", StringComparison.Ordinal))
                errors.Add($"role '{Show(r.Name)}': unknown model alias '{Show(r.Model)}' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)");
            if (r.EscalateTo is { } e && s.Roles.FirstOrDefault(x => x.Name == e) is not { Kind: RoleKind.Llm })
                errors.Add($"role '{Show(r.Name)}': escalate-to '{Show(e)}' is not an llm role");
            if (!used.Contains(r.Name)) errors.Add($"role '{Show(r.Name)}' is not used by the flow or any escalate-to");
        }

        foreach (var t in s.Tools)
        {
            if (string.IsNullOrWhiteSpace(t.Package)) errors.Add($"tool '{Show(t.Name)}': explicit package id required");
            else if (!PackageId.IsMatch(t.Package))
                errors.Add($"tool '{Show(t.Name)}': package '{Show(t.Package)}' is not a NuGet package id (letters, digits, '_', '.' or '-', starting with a letter or digit)");
            if (!IsPinnedVersion(t.Version)) errors.Add($"tool '{Show(t.Name)}': exact pinned version required (got '{Show(t.Version)}')");
        }

        foreach (var g in s.Gates)
        {
            if (g.Tool is { } gt && !s.Tools.Any(t => t.Name == gt))
                errors.Add($"gate '{Show(g.Name)}': tool '{Show(gt)}' does not exist");
        }

        foreach (var st in s.Flow)
        {
            var ok = st.Type switch
            {
                StageType.Fanout or StageType.Role => s.Roles.Any(r => r.Name == st.Target && r.Kind == RoleKind.Llm),
                StageType.Gate => s.Gates.Any(g => g.Name == st.Target),
                StageType.Tool => s.Tools.Any(t => t.Name == st.Target),
                _ => false
            };
            if (!ok) errors.Add($"flow stage '{Show(st.Target)}' ({st.Type}) does not exist");
        }
        return errors;
    }

    /// <summary>Returns the definition if valid, otherwise throws.</summary>
    /// <param name="s">The definition to validate.</param>
    /// <returns>The same definition.</returns>
    /// <exception cref="SwarmException">Thrown with the first validation error.</exception>
    public static SwarmDefinition Validated(SwarmDefinition s)
    {
        var e = Check(s);
        if (e.Count > 0) throw new SwarmException(e[0]);
        return s;
    }
}
