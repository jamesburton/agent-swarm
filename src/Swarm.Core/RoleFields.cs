namespace Swarm.Core;

/// <summary>Shared validation of the optional role fields (effort, isolation, maxTurns, context) used by every front-end.</summary>
public static class RoleFields
{
    static readonly HashSet<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    // Context modes the renderer implements; others are added when the renderer gives them an effect.
    static readonly string[] Contexts = ["distilled"];

    /// <summary>Validates the optional fields of a role.</summary>
    /// <param name="role">The role name (used in error messages).</param>
    /// <param name="effort">Raw effort text, or null.</param>
    /// <param name="isolation">Raw isolation text, or null.</param>
    /// <param name="maxTurns">Raw maxTurns text, or null.</param>
    /// <param name="context">Raw context text, or null.</param>
    /// <returns>The parsed turn limit, or null when <paramref name="maxTurns"/> is null.</returns>
    /// <exception cref="SwarmException">Thrown when a value is not allowed.</exception>
    public static int? Check(string role, string? effort, string? isolation, string? maxTurns, string? context)
    {
        if (effort != null && !Efforts.Contains(effort)) throw new SwarmException($"unknown effort '{SafeText.Show(effort)}' in role '{SafeText.Show(role)}'");
        if (isolation != null && isolation != "worktree") throw new SwarmException($"unsupported isolation '{SafeText.Show(isolation)}' in role '{SafeText.Show(role)}'");
        if (context != null && !Contexts.Contains(context))
            throw new SwarmException($"unsupported context '{SafeText.Show(context)}' in role '{SafeText.Show(role)}' (allowed: {string.Join(", ", Contexts)})");
        if (maxTurns == null) return null;
        return int.TryParse(maxTurns, out var n) && n > 0 ? n : throw new SwarmException($"bad maxTurns '{SafeText.Show(maxTurns)}' in role '{SafeText.Show(role)}'");
    }
}
