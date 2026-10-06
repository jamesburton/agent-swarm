using System.Text.RegularExpressions;

namespace Swarm.RunState;

/// <summary>Lander names for <see cref="SwarmConfig.Lander"/>.</summary>
public static class LanderNames
{
    /// <summary>One trailer-stamped commit per task (or per ticket inside a stack): the default.</summary>
    public const string Squash = "squash";

    /// <summary>Moves the epic to the tested integration commit (one merge commit per task).</summary>
    public const string FastForward = "fast-forward";
}

/// <summary>Values of <see cref="SquashConfig.Author"/>.</summary>
public static class SquashAuthorModes
{
    /// <summary>The author of the oldest squashed commit; other authors become <c>Co-authored-by</c> trailers.</summary>
    public const string Original = "original";

    /// <summary>The tool identity; every original author becomes a <c>Co-authored-by</c> trailer.</summary>
    public const string Tool = "tool";
}

/// <summary>The <c>squash</c> section of <c>.swarm/batch.json</c>.</summary>
public sealed record SquashConfig
{
    /// <summary>Regular expression matching a subject-template placeholder such as <c>{ticket}</c>.</summary>
    public const string PlaceholderPattern = @"\{([A-Za-z]+)\}";

    /// <summary>Gets the placeholder names a subject template may use.</summary>
    public static IReadOnlyList<string> Placeholders { get; } = ["ticket", "title", "taskId", "taskIds", "branch", "epic", "batch", "runId"];

    /// <summary>Gets the regular expression (named group <c>ticket</c>) applied to the branch, then the task id.</summary>
    public string TicketPattern { get; init; } = @"(?:^|/)(?<ticket>\d+)(?:-|$)";

    /// <summary>Gets a value indicating whether a task without a derivable ticket fails to land (instead of using its task id).</summary>
    public bool RequireTicket { get; init; }

    /// <summary>Gets the commit subject template.</summary>
    public string SubjectTemplate { get; init; } = "{ticket}: {title}";

    /// <summary>Gets who is recorded as author (<see cref="SquashAuthorModes"/>).</summary>
    public string Author { get; init; } = SquashAuthorModes.Original;

    /// <summary>Compiles a ticket pattern (culture-invariant, 1 s match timeout).</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The regex.</returns>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static Regex CompileTicketPattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Checks that a ticket pattern compiles and has a named group <c>ticket</c>.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>True when usable.</returns>
    public static bool IsValidTicketPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            return CompileTicketPattern(pattern).GetGroupNames().Contains("ticket", StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Finds the first placeholder in a template that is not in <see cref="Placeholders"/>.</summary>
    /// <param name="template">The template.</param>
    /// <returns>The unknown name, or null.</returns>
    public static string? FirstUnknownPlaceholder(string template) =>
        Regex.Matches(template, PlaceholderPattern, RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(name => !Placeholders.Contains(name, StringComparer.Ordinal));

    /// <summary>Lists every validation error of a squash section.</summary>
    /// <param name="config">The section.</param>
    /// <returns>Errors, empty when valid.</returns>
    public static IReadOnlyList<string> Check(SquashConfig config)
    {
        var e = new List<string>();
        if (!IsValidTicketPattern(config.TicketPattern))
        {
            e.Add($"squash.ticketPattern must be a valid regular expression with a named group 'ticket' (got '{config.TicketPattern}')");
        }

        if (string.IsNullOrWhiteSpace(config.SubjectTemplate) || config.SubjectTemplate.Any(char.IsControl))
        {
            e.Add("squash.subjectTemplate must be one non-empty line");
        }
        else if (FirstUnknownPlaceholder(config.SubjectTemplate) is { } unknown)
        {
            e.Add($"squash.subjectTemplate has unknown placeholder '{{{unknown}}}' (known: {string.Join(", ", Placeholders.Select(p => "{" + p + "}"))})");
        }

        if (config.Author is not (SquashAuthorModes.Original or SquashAuthorModes.Tool))
        {
            e.Add($"squash.author must be '{SquashAuthorModes.Original}' or '{SquashAuthorModes.Tool}' (got '{config.Author}')");
        }

        return e;
    }
}
