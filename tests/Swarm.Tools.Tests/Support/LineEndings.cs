using Swarm.Git;

namespace Swarm.Tools.Tests.Support;

/// <summary>
/// Repositories with the user's line-ending settings. <see cref="TempRepo"/> sets <c>core.autocrlf=false</c>, which hides
/// the bug class where the tools judge a user's clean CRLF checkout as modified; these helpers recreate the Git for Windows
/// default (<c>core.autocrlf=true</c>) repo-locally.
/// </summary>
public static class LineEndings
{
    /// <summary>Sets repo-local <c>core.autocrlf=true</c> (shared by every worktree of the repository).</summary>
    /// <param name="repo">The repository.</param>
    public static void UseAutoCrlf(TempRepo repo) => repo.Git("config", "core.autocrlf", "true");

    /// <summary>
    /// Makes a tracked LF file a clean CRLF checkout, as git writes it with <c>core.autocrlf=true</c> (for example after
    /// <c>git checkout -- file</c>): CRLF bytes on disk, the LF blob unchanged, the index stat refreshed, then a later mtime
    /// (as a touch or an editor save leaves) so the next git call re-reads the file instead of trusting the stat.
    /// </summary>
    /// <param name="worktree">The worktree holding the file.</param>
    /// <param name="relativePath">Tracked path relative to the worktree.</param>
    /// <exception cref="InvalidOperationException">The setup did not produce a clean CRLF checkout of the same blob.</exception>
    public static void CleanCrlfCheckout(string worktree, string relativePath)
    {
        var full = Path.Combine(worktree, relativePath);
        var repoGit = new GitRunner(worktree).WithRepoLineEndings();
        var blob = repoGit.Run("rev-parse", "HEAD:" + relativePath.Replace('\\', '/'));
        File.WriteAllText(full, File.ReadAllText(full).ReplaceLineEndings("\r\n"));
        repoGit.Run("add", "--", relativePath);
        if (repoGit.Run("ls-files", "-s", "--", relativePath).Split(' ')[1] != blob || !File.ReadAllText(full).Contains("\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{relativePath}' is not a clean CRLF checkout of {blob} (is core.autocrlf=true set?)");
        }

        Touch(full);
    }

    /// <summary>Moves a file's mtime past both its current value and now, so git re-reads it on the next call.</summary>
    /// <param name="file">The file.</param>
    public static void Touch(string file)
    {
        var now = DateTime.UtcNow;
        var current = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, (current > now ? current : now).AddSeconds(3));
    }

    /// <summary>Reports whether git under the tools' fixed <c>core.autocrlf=false</c> sees the file as modified.</summary>
    /// <param name="worktree">The worktree.</param>
    /// <param name="relativePath">Tracked path.</param>
    /// <returns>True when <c>git -c core.autocrlf=false status</c> lists it.</returns>
    public static bool ModifiedUnderAutoCrlfOff(string worktree, string relativePath) =>
        new GitRunner(worktree).Run("status", "--porcelain", "--untracked-files=no", "--", relativePath).Length > 0;
}
