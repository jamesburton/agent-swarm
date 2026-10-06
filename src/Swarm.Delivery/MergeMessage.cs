using System.Text;

namespace Swarm.Delivery;

/// <summary>Builds the <c>--no-ff</c> merge message of an epic close.</summary>
public static class MergeMessage
{
    /// <summary>The <c>Batch:</c> value of a manual <c>squash run</c>.</summary>
    public const string ManualBatch = "0";

    /// <summary>Builds the message.</summary>
    /// <param name="epic">The epic.</param>
    /// <param name="batchEpic">The batch epic id of the epic branch (what the squash lander stamps in <c>Epic:</c>), or null.</param>
    /// <param name="into">Target branch.</param>
    /// <param name="commits">Commits being merged, oldest first.</param>
    /// <returns>LF-separated message without a trailing newline.</returns>
    public static string Build(EpicRecord epic, string? batchEpic, string into, IReadOnlyList<TrailerCommit> commits)
    {
        var sb = new StringBuilder();
        sb.Append($"Merge epic {epic.Id}-{epic.Slug} ({epic.Branch}) into {into}\n\n");
        sb.Append(batchEpic is not null && batchEpic != epic.Id ? $"Epic: {epic.Id} (batch epic {batchEpic})\n" : $"Epic: {epic.Id}\n");
        var tickets = Tickets(commits);
        sb.Append(tickets.Count > 0 ? $"Tickets: {string.Join(", ", tickets)}\n" : "Tickets: none (no Ticket: trailers found)\n");

        // Batch numbers restart in every run, so the header counts runs instead of listing batch numbers.
        var runs = commits.SelectMany(c => c.Runs).Distinct(StringComparer.Ordinal).Count();
        if (runs > 0)
        {
            sb.Append($"Runs: {runs}\n");
        }

        var ticketed = commits.Where(c => c.Tickets.Count > 0).ToList();
        if (ticketed.Count > 0)
        {
            sb.Append('\n');
            foreach (var c in ticketed)
            {
                var batch = c.Batches.Count > 0 ? $" ({string.Join('+', c.Batches.Select(b => b == ManualBatch ? "manual" : "batch " + b))})" : "";
                sb.Append($"- {string.Join('+', c.Tickets)}{batch}: {Title(c)}\n");
            }
        }

        var untracked = commits.Count(c => c.Tickets.Count == 0);
        var foreign = commits.Where(c => c.Epics.Count > 0 && !c.Epics.Any(e => e == epic.Id || e == batchEpic)).ToList();
        if (untracked > 0 || foreign.Count > 0)
        {
            sb.Append('\n');
        }

        if (untracked > 0)
        {
            sb.Append($"Commits without a Ticket: trailer: {untracked}\n");
        }

        if (foreign.Count > 0)
        {
            sb.Append("Commits naming another epic: ").Append(string.Join(", ", foreign.Select(c => $"{c.Sha[..7]} (Epic: {string.Join('+', c.Epics)})"))).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The subject without a leading ticket followed by ':', space or '-' (the squash lander's subject is <c>{ticket}: {title}</c>).</summary>
    /// <param name="commit">The commit.</param>
    /// <returns>The title; the whole subject when nothing would be left.</returns>
    public static string Title(TrailerCommit commit)
    {
        foreach (var ticket in commit.Tickets)
        {
            if (!commit.Subject.StartsWith(ticket, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = commit.Subject[ticket.Length..];
            var title = rest.TrimStart(':', ' ', '-');
            if (rest.Length > 0 && (rest[0] is ':' or ' ' or '-') && title.Length > 0)
            {
                return title;
            }
        }

        return commit.Subject;
    }

    /// <summary>Lists ticket ids in first-seen order.</summary>
    /// <param name="commits">Commits, oldest first.</param>
    /// <returns>Distinct ticket ids.</returns>
    public static IReadOnlyList<string> Tickets(IReadOnlyList<TrailerCommit> commits) =>
        commits.SelectMany(c => c.Tickets).Distinct(StringComparer.Ordinal).ToList();
}
