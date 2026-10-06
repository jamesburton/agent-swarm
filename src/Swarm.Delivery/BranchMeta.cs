using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>What <c>worktree create</c> records about a task branch (stored in git config, removed by <c>git branch -D</c>).</summary>
/// <param name="Branch">Task branch (short name).</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Base">Branch it was created from (the epic branch).</param>
/// <param name="ForkPoint">Base commit at creation; head equal to it means "no commits yet".</param>
public sealed record BranchMeta(string Branch, string Ticket, string Base, string ForkPoint);

/// <summary>Reads and writes <see cref="BranchMeta"/> as <c>branch.&lt;b&gt;.swarm-*</c> config entries.</summary>
public static class BranchMetaStore
{
    const string TicketKey = "swarm-ticket";
    const string BaseKey = "swarm-base";
    const string ForkKey = "swarm-fork-point";

    /// <summary>Writes the metadata.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <param name="meta">The metadata.</param>
    public static void Write(GitRunner git, BranchMeta meta)
    {
        git.Run("config", $"branch.{meta.Branch}.{TicketKey}", meta.Ticket);
        git.Run("config", $"branch.{meta.Branch}.{BaseKey}", meta.Base);
        git.Run("config", $"branch.{meta.Branch}.{ForkKey}", meta.ForkPoint);
    }

    /// <summary>Reads the metadata of every branch that has all three entries.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <returns>Branch name to metadata.</returns>
    public static IReadOnlyDictionary<string, BranchMeta> ReadAll(GitRunner git)
    {
        var r = git.Try("config", "--get-regexp", @"^branch\..*\.swarm-");
        if (r.ExitCode == 1)
        {
            return new Dictionary<string, BranchMeta>(StringComparer.Ordinal);
        }

        // Any other failure is reported by Run with git's one-line error.
        var lines = r.ExitCode == 0 ? TextLines.Split(r.StdOut) : git.Lines("config", "--get-regexp", @"^branch\..*\.swarm-");
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // "branch.<name>.<key> <value>"; git lower-cases the key but keeps the branch (subsection) as is.
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                continue;
            }

            var name = line[..space];
            var dot = name.LastIndexOf('.');
            var branch = name["branch.".Length..dot];
            var key = name[(dot + 1)..];
            if (!fields.TryGetValue(branch, out var map))
            {
                fields[branch] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            map[key] = line[(space + 1)..];
        }

        return fields
            .Where(kv => kv.Value.ContainsKey(TicketKey) && kv.Value.ContainsKey(BaseKey) && kv.Value.ContainsKey(ForkKey))
            .ToDictionary(kv => kv.Key, kv => new BranchMeta(kv.Key, kv.Value[TicketKey], kv.Value[BaseKey], kv.Value[ForkKey]), StringComparer.Ordinal);
    }
}
