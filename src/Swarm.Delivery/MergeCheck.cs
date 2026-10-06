using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>How a branch was found to be landed.</summary>
public static class MergeVia
{
    /// <summary>Batch run state recorded it landed.</summary>
    public const string Ledger = "ledger";

    /// <summary>Its tip is an ancestor of the target.</summary>
    public const string Ancestor = "ancestor";

    /// <summary>Merging it into the target changes nothing (e.g. squash-landed).</summary>
    public const string Content = "content";
}

/// <summary>Decides whether a branch's work is already on a target branch.</summary>
public static class MergeCheck
{
    /// <summary>Checks ledger, ancestry, then content (merge-tree; git &gt;= 2.38, otherwise treated as not merged).</summary>
    /// <param name="git">Runner in any worktree.</param>
    /// <param name="branch">Branch to check (short name).</param>
    /// <param name="target">Target branch (short name).</param>
    /// <param name="ledgerBranches">Branches recorded as landed by batch runs.</param>
    /// <returns>A <see cref="MergeVia"/> value, or null when not merged.</returns>
    public static string? LandedVia(GitRunner git, string branch, string target, IReadOnlySet<string> ledgerBranches)
    {
        if (ledgerBranches.Contains(branch))
        {
            return MergeVia.Ledger;
        }

        var b = GitRunner.HeadsRef(branch);
        var t = GitRunner.HeadsRef(target);
        if (git.Try("merge-base", "--is-ancestor", b, t).ExitCode == 0)
        {
            return MergeVia.Ancestor;
        }

        // Exit 1 = conflicts; exit 129 = old git without --write-tree. Both mean "not provably merged".
        var merged = git.Try("merge-tree", "--write-tree", t, b);
        return merged.ExitCode == 0 && TextLines.Split(merged.StdOut).FirstOrDefault() == git.Run("rev-parse", t + "^{tree}")
            ? MergeVia.Content
            : null;
    }
}
