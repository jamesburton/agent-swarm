using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Batching;

/// <summary>A unit that could not be merged (or landed).</summary>
/// <param name="Unit">The whole unit (all members go back).</param>
/// <param name="Offender">The member whose merge failed.</param>
/// <param name="Files">Conflicting files.</param>
/// <param name="GitOutput">Git's output.</param>
/// <param name="MergedBefore">Task ids already in the tested state when it failed.</param>
public sealed record UnitConflict(TaskUnit Unit, TaskSpec Offender, IReadOnlyList<string> Files, string GitOutput, IReadOnlyList<string> MergedBefore);

/// <summary>Result of building an integration state.</summary>
/// <param name="Head">Integration commit (epic tip plus merged units).</param>
/// <param name="Merged">Units merged, in order.</param>
/// <param name="Conflicts">Units returned.</param>
public sealed record IntegrationResult(string Head, IReadOnlyList<TaskUnit> Merged, IReadOnlyList<UnitConflict> Conflicts);

/// <summary>Result of rebasing a copy ref.</summary>
/// <param name="Clean">True when the rebase succeeded.</param>
/// <param name="Output">Git's output.</param>
public sealed record RebaseOutcome(bool Clean, string Output);

/// <summary>The tool-owned, detached integration worktree of one epic (also used for rebases).</summary>
public sealed class IntegrationWorktree
{
    readonly GitRunner repo;

    /// <summary>Initializes a new instance of the <see cref="IntegrationWorktree"/> class.</summary>
    /// <param name="repo">Runner in the main worktree.</param>
    /// <param name="worktreePath">Absolute worktree path.</param>
    /// <exception cref="ToolException">Path too long (exit code 2).</exception>
    public IntegrationWorktree(GitRunner repo, string worktreePath)
    {
        this.repo = repo;
        WorktreePath = StatePaths.Guard(Path.GetFullPath(worktreePath), "integration worktree");
        Git = repo.At(WorktreePath).WithIdentity();
    }

    /// <summary>Gets the worktree path.</summary>
    public string WorktreePath { get; }

    /// <summary>Gets a runner in the worktree that commits with the tool identity.</summary>
    public GitRunner Git { get; }

    /// <summary>Creates the worktree detached at the epic, or reuses and cleans an existing one.</summary>
    /// <param name="epicBranch">Epic branch.</param>
    /// <exception cref="ToolException">The path is a non-empty directory that is not a worktree of this repo, or git fails (exit code 4).</exception>
    public void Ensure(string epicBranch)
    {
        repo.Run("worktree", "prune");
        if (!IsRegistered())
        {
            if (Directory.Exists(WorktreePath) && Directory.EnumerateFileSystemEntries(WorktreePath).Any())
            {
                throw new ToolException(ExitCodes.Environment, $"'{WorktreePath}' exists but is not a worktree of this repository", "remove it or set worktreeRoot in .swarm/batch.json");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(WorktreePath)!);
            repo.Run("worktree", "add", "-q", "--detach", WorktreePath, GitRunner.HeadsRef(epicBranch));
            return;
        }

        // Leftovers from a crashed run. Safe: only this tool uses the worktree and the caller holds the epic's batch lock.
        var gitDir = Git.Run("rev-parse", "--absolute-git-dir");
        foreach (var name in new[] { "index.lock", "HEAD.lock" })
        {
            var file = Path.Combine(gitDir, name);
            if (File.Exists(file))
            {
                SharedFile.Retry(() => File.Delete(file));
            }
        }

        Git.Try("merge", "--abort");
        Git.Try("rebase", "--abort");
        ResetTo(GitRunner.HeadsRef(epicBranch));
    }

    /// <summary>Detaches at a commit and removes untracked files (ignored build output is kept warm).</summary>
    /// <param name="commitish">Target.</param>
    public void ResetTo(string commitish)
    {
        Git.Run("checkout", "-q", "-f", "--detach", commitish);
        Git.Run("clean", "-q", "-fd");
    }

    /// <summary>Builds epic tip + sequential <c>merge --no-ff</c> of each unit; a conflicting unit is rolled back and skipped.</summary>
    /// <param name="epicTip">Starting commit.</param>
    /// <param name="units">Units in order.</param>
    /// <returns>The integration result.</returns>
    public IntegrationResult Integrate(string epicTip, IReadOnlyList<TaskUnit> units)
    {
        ResetTo(epicTip);
        var merged = new List<TaskUnit>();
        var conflicts = new List<UnitConflict>();
        foreach (var unit in units)
        {
            var before = Git.RevParse("HEAD");
            UnitConflict? conflict = null;
            foreach (var task in unit.Members)
            {
                var r = Git.Try("merge", "--no-ff", "--no-edit", "-m", $"batch: merge {task.Id} ({task.Branch})", task.BranchRef);
                if (r.ExitCode == 0)
                {
                    continue;
                }

                var files = Git.Lines("diff", "--name-only", "--diff-filter=U");
                Git.Try("merge", "--abort");
                conflict = new UnitConflict(unit, task, files, (r.StdOut + r.StdErr).Trim(), merged.SelectMany(u => u.Ids).ToList());
                break;
            }

            if (conflict is null)
            {
                merged.Add(unit);
            }
            else
            {
                ResetTo(before);
                conflicts.Add(conflict);
            }
        }

        return new IntegrationResult(Git.RevParse("HEAD"), merged, conflicts);
    }

    /// <summary>Rebases a COPY of the task branch onto the epic tip; the worker's branch is never modified.</summary>
    /// <param name="task">The task (its <see cref="TaskSpec.Branch"/> is the worker's branch).</param>
    /// <param name="epicTip">Rebase target.</param>
    /// <param name="copyBranch">Copy ref name, e.g. <c>rebased/E1/T2</c>.</param>
    /// <returns>Clean or not, with git's output; on conflict the copy is deleted.</returns>
    public RebaseOutcome RebaseCopy(TaskSpec task, string epicTip, string copyBranch)
    {
        ResetTo(epicTip);
        Git.Run("checkout", "-q", "-f", "-B", copyBranch, task.BranchRef);
        var r = Git.Try("rebase", epicTip);
        var output = (r.StdOut + r.StdErr).Trim();
        if (r.ExitCode != 0)
        {
            Git.Try("rebase", "--abort");
            ResetTo(epicTip);
            Git.Try("branch", "-D", copyBranch);
            return new RebaseOutcome(false, output);
        }

        // Detach so the copy branch is not checked out here.
        ResetTo(epicTip);
        return new RebaseOutcome(true, output);
    }

    bool IsRegistered()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return repo.Lines("worktree", "list", "--porcelain")
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Any(l => string.Equals(Path.GetFullPath(l["worktree ".Length..]).TrimEnd('\\', '/'), WorktreePath.TrimEnd('\\', '/'), comparison));
    }
}
