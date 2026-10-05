using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>One record of <c>git worktree list --porcelain</c>.</summary>
/// <param name="Path">Absolute path, normalised for this OS.</param>
/// <param name="Head">HEAD commit, if reported.</param>
/// <param name="Branch">Checked-out branch (short name), or null when detached.</param>
/// <param name="Detached">True for a detached HEAD.</param>
/// <param name="Locked">True when locked (<c>git worktree lock</c>).</param>
/// <param name="LockReason">Lock reason, if one was given.</param>
/// <param name="Prunable">True when git reports the worktree directory missing.</param>
public sealed record GitWorktree(string Path, string? Head, string? Branch, bool Detached, bool Locked, string? LockReason, bool Prunable);

/// <summary>Reads git's worktree list.</summary>
public static class WorktreeList
{
    const string BranchPrefix = "branch refs/heads/";

    /// <summary>Parses porcelain output (records start at each <c>worktree </c> line; blank lines are optional).</summary>
    /// <param name="porcelainLines">Output lines.</param>
    /// <returns>The worktrees in git's order.</returns>
    public static IReadOnlyList<GitWorktree> Parse(IEnumerable<string> porcelainLines)
    {
        var list = new List<GitWorktree>();
        GitWorktree? current = null;
        foreach (var raw in porcelainLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    list.Add(current);
                }

                current = new GitWorktree(System.IO.Path.GetFullPath(line["worktree ".Length..]), null, null, false, false, null, false);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                current = current with { Head = line["HEAD ".Length..] };
            }
            else if (line.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                current = current with { Branch = line[BranchPrefix.Length..] };
            }
            else if (line == "detached")
            {
                current = current with { Detached = true };
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                current = current with { Locked = true, LockReason = line.Length > "locked ".Length ? line["locked ".Length..] : null };
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                current = current with { Prunable = true };
            }
        }

        if (current is not null)
        {
            list.Add(current);
        }

        return list;
    }

    /// <summary>Runs <c>git worktree list --porcelain</c>.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <returns>The worktrees.</returns>
    public static IReadOnlyList<GitWorktree> Read(GitRunner git) => Parse(git.Lines("worktree", "list", "--porcelain"));

    /// <summary>Finds the worktree where a branch is checked out.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <param name="branch">Short branch name.</param>
    /// <returns>The worktree, or null.</returns>
    public static GitWorktree? CheckedOut(GitRunner git, string branch) =>
        Read(git).FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.Ordinal));

    /// <summary>Compares paths after normalisation (case-insensitive on Windows).</summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns>True when they name the same location.</returns>
    public static bool SamePath(string a, string b) =>
        string.Equals(
            System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'),
            System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
