using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Resolves and guards run-state and worktree paths.</summary>
public static class StatePaths
{
    /// <summary>Longest accepted state-dir or worktree path: Windows MAX_PATH (260) breaks git and dotnet children inside it.</summary>
    public const int MaxPathLength = 200;

    /// <summary>Resolves the state directory: relative paths are under the MAIN worktree, so every worktree shares one state.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="configured">Configured state dir (relative or absolute).</param>
    /// <returns>The guarded absolute path.</returns>
    /// <exception cref="ToolException">Empty or too long (exit code 2).</exception>
    public static string Resolve(RepoPaths repo, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new ToolException(ExitCodes.Usage, "stateDir must not be empty");
        }

        var full = Path.IsPathFullyQualified(configured) ? configured : Path.Combine(repo.MainWorktreeRoot, configured);
        return Guard(Path.GetFullPath(full), "state dir");
    }

    /// <summary>Resolves the worktree root (default: a sibling of the main worktree named <c>&lt;repo&gt;-wt</c>).</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="configured">Configured absolute root, or null.</param>
    /// <returns>The guarded absolute path.</returns>
    /// <exception cref="ToolException">Too long (exit code 2).</exception>
    public static string ResolveWorktreeRoot(RepoPaths repo, string? configured)
    {
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(repo.MainWorktreeRoot) ?? repo.MainWorktreeRoot, Path.GetFileName(repo.MainWorktreeRoot) + "-wt")
            : configured;
        return Guard(Path.GetFullPath(root), "worktree root");
    }

    /// <summary>Rejects over-long paths before anything is created.</summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <param name="what">Name used in the message (e.g. "state dir").</param>
    /// <returns><paramref name="fullPath"/>.</returns>
    /// <exception cref="ToolException">Longer than <see cref="MaxPathLength"/> (exit code 2).</exception>
    public static string Guard(string fullPath, string what) =>
        fullPath.Length <= MaxPathLength
            ? fullPath
            : throw new ToolException(ExitCodes.Usage, $"{what} path is {fullPath.Length} chars (limit {MaxPathLength})", "use a shorter path; Windows MAX_PATH breaks git and dotnet children");
}

/// <summary>Layout of a run-state directory.</summary>
/// <param name="Root">The state directory.</param>
public sealed record StateLayout(string Root)
{
    /// <summary>Gets the slot lock directory (one <c>slot-k.lock</c> per held slot).</summary>
    public string SlotsDir => Path.Combine(Root, "slots");

    /// <summary>Gets the directory of per-run folders.</summary>
    public string RunsDir => Path.Combine(Root, "runs");

    /// <summary>Gets the testgate event log.</summary>
    public string GateEventsFile => Path.Combine(Root, "testgate.events.jsonl");

    /// <summary>Gets the lock directory that serialises batch runs of one epic.</summary>
    /// <param name="epic">Epic id.</param>
    /// <returns>The directory.</returns>
    public string BatchLockDir(string epic) => Path.Combine(Root, "locks", "batch-" + epic);

    /// <summary>Gets one run's folder.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>The directory.</returns>
    public string RunDir(string runId) => Path.Combine(RunsDir, runId);
}
