namespace Swarm.Git;

/// <summary>Small text helpers for one-line messages and line lists.</summary>
public static class TextLines
{
    /// <summary>Maximum length of a one-line message.</summary>
    public const int MaxOneLineLength = 400;

    static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    /// <summary>Collapses all whitespace runs (including line breaks) to single spaces and truncates.</summary>
    /// <param name="text">Any text.</param>
    /// <returns>A single line of at most <see cref="MaxOneLineLength"/> characters.</returns>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var collapsed = string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxOneLineLength ? collapsed : string.Concat(collapsed.AsSpan(0, MaxOneLineLength - 3), "...");
    }

    /// <summary>Splits command output into trimmed, non-empty lines.</summary>
    /// <param name="text">Output text (LF or CRLF).</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
