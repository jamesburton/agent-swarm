using Swarm.Git;

namespace Swarm.Batching;

/// <summary>Pre-run repository checks.</summary>
public static class RepoChecks
{
    /// <summary>Requires the epic branch to exist and not be checked out anywhere (batch moves it by ref).</summary>
    /// <param name="repo">Runner in the main worktree.</param>
    /// <param name="epicBranch">Epic branch name.</param>
    /// <returns>The epic tip sha.</returns>
    /// <exception cref="ToolException">Missing or checked out (exit code 3).</exception>
    public static string EnsureEpic(GitRunner repo, string epicBranch)
    {
        var fullRef = GitRunner.HeadsRef(epicBranch);
        if (!repo.RefExists(fullRef))
        {
            throw new ToolException(ExitCodes.BadInput, $"epic branch '{epicBranch}' not found", $"create it first, e.g. git branch {epicBranch} main");
        }

        string? current = null;
        foreach (var line in repo.Lines("worktree", "list", "--porcelain"))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line["worktree ".Length..];
            }
            else if (line == "branch " + fullRef)
            {
                throw new ToolException(ExitCodes.BadInput, $"epic branch '{epicBranch}' is checked out in '{current}'", "batch moves the epic by ref; check out another branch there");
            }
        }

        return repo.RevParse(fullRef);
    }
}
