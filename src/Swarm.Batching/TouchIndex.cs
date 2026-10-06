using Swarm.Git;

namespace Swarm.Batching;

/// <summary>Files each task changes, derived from git (never trusted from the tasks file).</summary>
/// <param name="repo">Runner in the main worktree.</param>
public sealed class TouchIndex(GitRunner repo)
{
    static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, IReadOnlySet<string>> touches = new(StringComparer.Ordinal);

    /// <summary>Derives changed files: <c>git diff --name-only --no-renames from...branch</c> (since the merge base).</summary>
    /// <param name="fromRef">Usually the epic tip.</param>
    /// <param name="branchRef">The task branch ref.</param>
    /// <returns>Case-insensitive set of repo-relative paths.</returns>
    public IReadOnlySet<string> Derive(string fromRef, string branchRef)
    {
        // --no-renames: a rename reports both paths. -z: NUL-separated, never C-quoted. Raw stdout (Run would trim a leading space).
        var r = repo.Try("diff", "--name-only", "--no-renames", "-z", $"{fromRef}...{branchRef}");
        if (r.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Environment, $"git diff {fromRef}...{branchRef} failed: {TextLines.OneLine(r.StdErr)}");
        }

        return r.StdOut.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records a task's files.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="files">Its files.</param>
    public void Set(string taskId, IReadOnlySet<string> files) => touches[taskId] = files;

    /// <summary>Gets a task's files.</summary>
    /// <param name="taskId">Task id.</param>
    /// <returns>The files, or an empty set.</returns>
    public IReadOnlySet<string> Get(string taskId) => touches.TryGetValue(taskId, out var f) ? f : None;

    /// <summary>Checks whether two units change a common file.</summary>
    /// <param name="a">First unit.</param>
    /// <param name="b">Second unit.</param>
    /// <returns>True on overlap.</returns>
    public bool Overlaps(TaskUnit a, TaskUnit b) => a.Ids.Any(x => b.Ids.Any(y => Get(x).Overlaps(Get(y))));

    /// <summary>Finds which candidate tasks change any of the given files.</summary>
    /// <param name="candidateIds">Tasks already in the tested state.</param>
    /// <param name="files">Conflicting files.</param>
    /// <returns>Distinct matching ids in candidate order.</returns>
    public IReadOnlyList<string> Partners(IEnumerable<string> candidateIds, IReadOnlyCollection<string> files) =>
        candidateIds.Distinct(StringComparer.Ordinal).Where(id => Get(id).Overlaps(files)).ToList();

    /// <summary>Copies the index for the summary.</summary>
    /// <returns>Task id to sorted files.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Snapshot() =>
        touches.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
}
