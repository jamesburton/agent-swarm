namespace Swarm.Git;

/// <summary>A failure a swarm tool reports as one stderr line and a specific exit code.</summary>
public sealed class ToolException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ToolException"/> class.</summary>
    /// <param name="exitCode">The exit code to use (see <see cref="ExitCodes"/>).</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="hint">Optional short advice, printed in parentheses.</param>
    public ToolException(int exitCode, string message, string? hint = null)
        : base(message)
    {
        ExitCode = exitCode;
        Hint = hint;
    }

    /// <summary>Gets the exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the optional hint.</summary>
    public string? Hint { get; }

    /// <summary>Gets the one-line description: <c>&lt;what&gt; (&lt;hint&gt;)</c>.</summary>
    public string Summary => Describe(Message, Hint);

    /// <summary>Gets the stderr line: <c>error: &lt;what&gt; (&lt;hint&gt;)</c>.</summary>
    public string ErrorLine => "error: " + Summary;

    /// <summary>Builds the one-line description.</summary>
    /// <param name="message">What went wrong; line breaks are collapsed.</param>
    /// <param name="hint">Optional advice.</param>
    /// <returns>The description without the <c>error: </c> prefix.</returns>
    public static string Describe(string message, string? hint)
    {
        var what = TextLines.OneLine(message);
        return string.IsNullOrWhiteSpace(hint) ? what : $"{what} ({TextLines.OneLine(hint)})";
    }

    /// <summary>Formats a one-line error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="hint">Optional advice.</param>
    /// <returns>The formatted line, without a trailing newline.</returns>
    public static string Format(string message, string? hint = null) => "error: " + Describe(message, hint);
}
