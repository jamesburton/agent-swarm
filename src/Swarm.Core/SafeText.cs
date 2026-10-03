using System.Globalization;
using System.Text;

namespace Swarm.Core;

/// <summary>Character checks shared by the validator and the renderers: one-line display of untrusted values, and invisible characters.</summary>
public static class SafeText
{
    /// <summary>Renders a value for an error message on one line: control, line-separator, format and surrogate characters become <c>?</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The single-line text.</returns>
    public static string Show(string value) =>
        new(value.Select(c => IsUnsafeChar(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? '?' : c).ToArray());

    /// <summary>True for control characters and the Unicode line separators U+2028 and U+2029.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether it is unsafe in a one-line context.</returns>
    public static bool IsUnsafeChar(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';

    /// <summary>
    /// Finds the first invisible Unicode format character (category Format, for example the bidi controls U+202A-U+202E and U+2066-U+2069,
    /// the zero-width characters U+200B-U+200D, U+2060 and U+FEFF, and the tag characters U+E0000-U+E007F).
    /// </summary>
    /// <param name="value">The text to search.</param>
    /// <returns>The code point as <c>U+XXXX</c>, or null when there is none.</returns>
    public static string? FindFormatCharacter(string value)
    {
        foreach (var r in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(r) == UnicodeCategory.Format) return $"U+{r.Value:X4}";
        }

        return null;
    }
}
