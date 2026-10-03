using System.Text.RegularExpressions;
namespace Swarm.Core;

/// <summary>Thrown when a swarm definition is invalid.</summary>
/// <param name="message">The error message.</param>
public class SwarmException(string message) : Exception(message);

/// <summary>Validates a <see cref="SwarmDefinition"/>.</summary>
public static class Validator
{
    static readonly HashSet<string> Aliases = ["haiku", "sonnet", "opus", "fable", "inherit"];
    static readonly Regex Pinned = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");

    /// <summary>Returns all validation errors (empty when valid).</summary>
    /// <param name="s">The definition to check.</param>
    /// <returns>The error messages.</returns>
    public static IReadOnlyList<string> Check(SwarmDefinition s)
    {
        var errors = new List<string>();
        var names = s.Roles.Select(r => r.Name).Concat(s.Tools.Select(t => t.Name)).Concat(s.Gates.Select(g => g.Name));
        foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"duplicate name '{dup.Key}'");

        if (s.Roles.Count(r => r.Kind == RoleKind.Code) != 1)
            errors.Add("expected exactly one code orchestrator role");

        foreach (var r in s.Roles.Where(r => r.Kind == RoleKind.Llm))
        {
            if (string.IsNullOrWhiteSpace(r.Model)) errors.Add($"role '{r.Name}': missing required model");
            else if (!Aliases.Contains(r.Model) && !r.Model.StartsWith("claude-", StringComparison.Ordinal))
                errors.Add($"role '{r.Name}': unknown model alias '{r.Model}' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)");
            if (r.EscalateTo is { } e && s.Roles.FirstOrDefault(x => x.Name == e) is not { Kind: RoleKind.Llm })
                errors.Add($"role '{r.Name}': escalate-to '{e}' is not an llm role");
        }

        foreach (var t in s.Tools)
        {
            if (string.IsNullOrWhiteSpace(t.Package)) errors.Add($"tool '{t.Name}': explicit package id required");
            if (!Pinned.IsMatch(t.Version ?? "")) errors.Add($"tool '{t.Name}': exact pinned version required (got '{t.Version}')");
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
            if (!ok) errors.Add($"flow stage '{st.Target}' ({st.Type}) does not exist");
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
