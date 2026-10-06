namespace Swarm.Git;

/// <summary>Where a repository lives on disk.</summary>
/// <param name="WorktreeRoot">Root of the worktree containing the start directory.</param>
/// <param name="MainWorktreeRoot">Root of the main worktree (shared run state and config live here).</param>
/// <param name="CommonGitDir">The repository's common git directory.</param>
public sealed record RepoPaths(string WorktreeRoot, string MainWorktreeRoot, string CommonGitDir);

/// <summary>Finds the current and main worktree of a repository.</summary>
public static class RepoLocator
{
    /// <summary>Locates the repository containing a directory.</summary>
    /// <param name="startDirectory">Any directory inside a worktree.</param>
    /// <returns>The paths.</returns>
    /// <exception cref="ToolException">Not inside a worktree, or the repository has no main worktree (exit code 3).</exception>
    public static RepoPaths Locate(string startDirectory)
    {
        if (!Directory.Exists(startDirectory))
        {
            throw new ToolException(ExitCodes.BadInput, $"directory '{startDirectory}' does not exist");
        }

        var git = new GitRunner(startDirectory);
        var top = git.Try("rev-parse", "--show-toplevel");
        if (top.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.BadInput, $"'{startDirectory}' is not inside a git worktree", "run from a clone or one of its worktrees");
        }

        // All worktrees share one common dir; for a normal clone it is <main worktree>/.git.
        var common = Path.GetFullPath(git.Run("rev-parse", "--path-format=absolute", "--git-common-dir")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(common), ".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ExitCodes.BadInput, $"repository '{common}' has no main worktree (bare or --separate-git-dir)", "run from a normal clone");
        }

        return new RepoPaths(Path.GetFullPath(top.StdOut.Trim()), Path.GetDirectoryName(common)!, common);
    }
}
