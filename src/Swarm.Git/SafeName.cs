using System.Text.RegularExpressions;

namespace Swarm.Git;

/// <summary>Names that are safe as file names, ref components and run ids (epic ids, task ids, run ids).</summary>
public static partial class SafeName
{
    /// <summary>Human description used in error messages.</summary>
    public const string Description = "letters, digits, '_', '-' and single dots, starting with a letter or digit, not ending in '.lock', at most 100 chars";

    /// <summary>Maximum length.</summary>
    public const int MaxLength = 100;

    /// <summary>Checks a name.</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>True when the name is safe.</returns>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && Matcher().IsMatch(name)
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Matcher();
}
