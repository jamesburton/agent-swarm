using System.Text.RegularExpressions;
using Swarm.Core;

namespace Swarm.Render;

/// <summary>Validation of names that become file-name stems, and one-line rendering of untrusted values for messages.</summary>
internal static partial class SafeStems
{
    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // \z (not $) so a trailing newline cannot slip through.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex SafeStem();

    /// <summary>Checks that every name is a safe file stem and that no two collide case-insensitively.</summary>
    /// <param name="names">The names to check.</param>
    /// <param name="kind">Noun used in messages (for example <c>role</c>).</param>
    /// <exception cref="SwarmException">Thrown on the first unsafe or colliding name.</exception>
    public static void Validate(IEnumerable<string> names, string kind)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!SafeStem().IsMatch(name) || ReservedDeviceNames.Contains(name))
                throw new SwarmException($"{kind} '{Show(name)}': name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)");
            if (!seen.Add(name))
                throw new SwarmException($"{kind} '{Show(name)}': name collides case-insensitively with another {kind}");
        }
    }

    /// <summary>Renders a value for an error message on one line (control and line-separator characters replaced).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The single-line text.</returns>
    public static string Show(string value) => new(value.Select(c => IsUnsafeChar(c) ? '?' : c).ToArray());

    /// <summary>True for control characters and the Unicode line separators.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether it is unsafe in a one-line context.</returns>
    public static bool IsUnsafeChar(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
}
