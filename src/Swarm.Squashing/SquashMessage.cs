using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Squashing;

/// <summary>An original commit folded into a squashed commit.</summary>
/// <param name="Sha">Commit id.</param>
/// <param name="AuthorName">Author name.</param>
/// <param name="AuthorEmail">Author email.</param>
/// <param name="Subject">Subject line.</param>
public sealed record SourceCommit(string Sha, string AuthorName, string AuthorEmail, string Subject);

/// <summary>Tasks that become one squashed commit.</summary>
/// <param name="Ticket">Ticket for the <c>Ticket:</c> trailer.</param>
/// <param name="Tasks">Tasks in landing order.</param>
/// <param name="SourceTips">Per task, the tested branch commit, or null when its merge was a no-op.</param>
/// <param name="Commits">The tasks' original non-merge commits, oldest first.</param>
public sealed record SquashGroup(string Ticket, IReadOnlyList<LandTask> Tasks, IReadOnlyList<string?> SourceTips, IReadOnlyList<SourceCommit> Commits);

/// <summary>Batch-level values used in messages.</summary>
/// <param name="Epic">Epic id.</param>
/// <param name="Batch">Batch number (0 = manual <c>squash run</c>).</param>
/// <param name="RunId">Run id.</param>
public sealed record MessageContext(string Epic, int Batch, string RunId);

/// <summary>Builds squashed-commit messages: templated subject, optional commit list, fixed trailer block.</summary>
public static class SquashMessage
{
    static readonly Regex Placeholder = new(SquashConfig.PlaceholderPattern, RegexOptions.CultureInvariant);

    static readonly string ToolEmail = GitRunner.ToolIdentity.Single(a => a.StartsWith("user.email=", StringComparison.Ordinal))["user.email=".Length..];

    /// <summary>Picks the recorded author.</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="group">The group.</param>
    /// <returns>The oldest commit with an author name in <c>original</c> mode; null (tool identity) otherwise or when none has a name.</returns>
    public static SourceCommit? Author(SquashConfig config, SquashGroup group) =>
        config.Author == SquashAuthorModes.Original
            ? group.Commits.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.AuthorName))
            : null;

    /// <summary>Lists the other authors as <c>Name &lt;email&gt;</c>, distinct by email (case-insensitive), excluding the author and the tool.</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="group">The group.</param>
    /// <returns>Co-authors in commit order.</returns>
    public static IReadOnlyList<string> CoAuthors(SquashConfig config, SquashGroup group)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ToolEmail };
        if (Author(config, group) is { } author)
        {
            seen.Add(author.AuthorEmail);
        }

        return group.Commits
            .Where(c => !string.IsNullOrWhiteSpace(c.AuthorName) && seen.Add(c.AuthorEmail))
            .Select(c => $"{TextLines.OneLine(c.AuthorName)} <{c.AuthorEmail}>")
            .ToList();
    }

    /// <summary>The title: oldest commit subject without a leading ticket, else the first task's branch.</summary>
    /// <param name="group">The group.</param>
    /// <returns>One line.</returns>
    public static string Title(SquashGroup group)
    {
        var subject = TextLines.OneLine(group.Commits.FirstOrDefault()?.Subject);
        if (subject.StartsWith(group.Ticket, StringComparison.OrdinalIgnoreCase))
        {
            var rest = subject[group.Ticket.Length..];
            if (rest.Length == 0 || rest[0] is ':' or ' ' or '-')
            {
                subject = rest.TrimStart(':', ' ', '-');
            }
        }

        return subject.Length > 0 ? subject : group.Tasks[0].Branch;
    }

    /// <summary>Expands <see cref="SquashConfig.SubjectTemplate"/> in one pass (values are never expanded again).</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="group">The group.</param>
    /// <param name="context">Batch values.</param>
    /// <returns>The subject line.</returns>
    public static string Subject(SquashConfig config, SquashGroup group, MessageContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ticket"] = group.Ticket,
            ["title"] = Title(group),
            ["taskId"] = group.Tasks[0].Id,
            ["taskIds"] = string.Join('+', group.Tasks.Select(t => t.Id)),
            ["branch"] = group.Tasks[0].Branch,
            ["epic"] = context.Epic,
            ["batch"] = context.Batch.ToString(CultureInfo.InvariantCulture),
            ["runId"] = context.RunId,
        };
        return TextLines.OneLine(Placeholder.Replace(config.SubjectTemplate, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value));
    }

    /// <summary>Builds the full message (LF, trailing newline).</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="group">The group.</param>
    /// <param name="context">Batch values.</param>
    /// <returns>The message.</returns>
    public static string Build(SquashConfig config, SquashGroup group, MessageContext context)
    {
        var text = new StringBuilder();
        text.Append(Subject(config, group, context)).Append('\n');
        if (group.Commits.Count > 1)
        {
            text.Append("\nSquashed commits:\n");
            foreach (var c in group.Commits)
            {
                text.Append("- ").Append(TextLines.OneLine(c.Subject)).Append('\n');
            }
        }

        // One trailer paragraph at the end, fixed order, so `git interpret-trailers` and %(trailers) parse it.
        text.Append('\n');
        Trailer(text, "Ticket", group.Ticket);
        Trailer(text, "Epic", context.Epic);
        Trailer(text, "Batch", context.Batch.ToString(CultureInfo.InvariantCulture));
        Trailer(text, "Swarm-Run", context.RunId);
        for (var i = 0; i < group.Tasks.Count; i++)
        {
            Trailer(text, "Task", group.Tasks[i].Id);
            if (group.SourceTips[i] is { } source)
            {
                Trailer(text, "Source-Commit", source);
            }
        }

        foreach (var coAuthor in CoAuthors(config, group))
        {
            Trailer(text, "Co-authored-by", coAuthor);
        }

        return text.ToString();
    }

    static void Trailer(StringBuilder text, string key, string value) => text.Append(key).Append(": ").Append(TextLines.OneLine(value)).Append('\n');
}
