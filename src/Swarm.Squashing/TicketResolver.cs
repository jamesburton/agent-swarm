using System.Text.RegularExpressions;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Squashing;

/// <summary>Derives the ticket of a task for the <c>Ticket:</c> trailer.</summary>
public static class TicketResolver
{
    /// <summary>Matches <see cref="SquashConfig.TicketPattern"/> against the branch, then the task id.</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="taskId">Task id.</param>
    /// <param name="branch">Branch name (the worker branch for rebased copies).</param>
    /// <returns>The ticket, or null when neither matches.</returns>
    /// <exception cref="ToolException">The pattern timed out (exit code 2).</exception>
    public static string? Find(SquashConfig config, string taskId, string branch)
    {
        var regex = SquashConfig.CompileTicketPattern(config.TicketPattern);
        foreach (var candidate in new[] { branch, taskId })
        {
            Match match;
            try
            {
                match = regex.Match(candidate);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ToolException(ExitCodes.Usage, $"squash.ticketPattern timed out on '{candidate}'", "simplify the pattern");
            }

            var ticket = match.Success ? match.Groups["ticket"].Value.Trim() : "";
            if (ticket.Length > 0 && !ticket.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return ticket;
            }
        }

        return null;
    }

    /// <summary>Resolves a task's ticket: override, then <see cref="Find"/>, then the task id unless a ticket is required.</summary>
    /// <param name="config">Squash settings.</param>
    /// <param name="taskId">Task id.</param>
    /// <param name="branch">Branch name.</param>
    /// <param name="ticketOverride">Explicit ticket (CLI <c>--ticket</c>), or null.</param>
    /// <returns>The ticket, or null when <see cref="SquashConfig.RequireTicket"/> is set and none was found.</returns>
    public static string? Resolve(SquashConfig config, string taskId, string branch, string? ticketOverride) =>
        !string.IsNullOrWhiteSpace(ticketOverride)
            ? ticketOverride.Trim()
            : Find(config, taskId, branch) ?? (config.RequireTicket ? null : taskId);
}
