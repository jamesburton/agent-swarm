using Swarm.Git;

namespace Swarm.Squashing;

/// <summary>Checks that a rebuilt commit has exactly the tested tree (same blobs, modes and names) before any ref moves.</summary>
public static class TreeGuard
{
    /// <summary>Requires equal tree ids: the exact form of <c>git diff --exit-code &lt;expected&gt; &lt;actual&gt;</c>.</summary>
    /// <param name="repo">Runner in the main worktree.</param>
    /// <param name="actual">The rebuilt commit.</param>
    /// <param name="expected">The tested commit whose tree must be reproduced.</param>
    /// <param name="what">Who checks (used in the message).</param>
    /// <exception cref="ToolException">The trees differ (exit code 4); the message carries <c>git diff --stat</c>.</exception>
    public static void Ensure(GitRunner repo, string actual, string expected, string what)
    {
        var actualTree = repo.Run("rev-parse", actual + "^{tree}");
        var expectedTree = repo.Run("rev-parse", expected + "^{tree}");
        if (string.Equals(actualTree, expectedTree, StringComparison.Ordinal))
        {
            return;
        }

        var stat = TextLines.OneLine(repo.Try("diff", "--stat", expected, actual).StdOut);
        throw new ToolException(
            ExitCodes.Environment,
            $"{what}: rebuilt tree {actualTree} differs from the tested tree {expectedTree} ({stat}); the epic was not moved",
            "this is a lander bug: keep the run folder and report it");
    }
}
