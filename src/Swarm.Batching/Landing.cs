using Swarm.Git;

namespace Swarm.Batching;

/// <summary>A task to land.</summary>
/// <param name="Id">Task id.</param>
/// <param name="Branch">Branch that was tested (worker branch or rebased copy).</param>
/// <param name="DependsOn">Stack dependencies (all earlier in the same request).</param>
public sealed record LandTask(string Id, string Branch, IReadOnlyList<string> DependsOn);

/// <summary>Everything a lander needs. <c>TestedCommit</c> = <c>EpicTipBefore</c> + <c>merge --no-ff</c> of every task in order, and it was green.</summary>
/// <param name="Repo">Runner in the main worktree.</param>
/// <param name="Worktree">Runner in the idle integration worktree (tool identity); may be checked out detached anywhere.</param>
/// <param name="Epic">Epic id (for trailers).</param>
/// <param name="EpicBranch">Epic branch to move.</param>
/// <param name="EpicTipBefore">Expected current epic tip (compare-and-swap).</param>
/// <param name="TestedCommit">The green integration commit.</param>
/// <param name="Tasks">Tasks in landing order; stacks are contiguous and in order.</param>
/// <param name="Batch">Batch number (for trailers).</param>
/// <param name="RunId">Run id (for trailers and reflog).</param>
public sealed record LandRequest(GitRunner Repo, GitRunner Worktree, string Epic, string EpicBranch, string EpicTipBefore, string TestedCommit, IReadOnlyList<LandTask> Tasks, int Batch, string RunId);

/// <summary>A landed task.</summary>
/// <param name="TaskId">Task id.</param>
/// <param name="Commit">Epic commit that contains it.</param>
public sealed record LandedTask(string TaskId, string Commit);

/// <summary>The first task that could not land.</summary>
/// <param name="TaskId">Task id.</param>
/// <param name="Files">Conflicting files, if any.</param>
/// <param name="GitOutput">Git's output.</param>
public sealed record LandFailure(string TaskId, IReadOnlyList<string> Files, string GitOutput);

/// <summary>What a lander did.</summary>
/// <param name="EpicTipAfter">Epic tip after landing (must equal the epic ref).</param>
/// <param name="Landed">Landed tasks, in order.</param>
/// <param name="Failure">The first task that failed, or null.</param>
/// <param name="NotAttempted">Every task after the failure, in order.</param>
public sealed record LandResult(string EpicTipAfter, IReadOnlyList<LandedTask> Landed, LandFailure? Failure, IReadOnlyList<string> NotAttempted);

/// <summary>
/// Lands a green, tested set of tasks on the epic branch. Rules: land in request order and stop at the first failure;
/// move the epic only from <see cref="LandRequest.EpicTipBefore"/> (compare-and-swap); never touch task branches;
/// account for every task exactly once; when nothing failed, the landed tree must equal the tested tree.
/// </summary>
public interface ILander
{
    /// <summary>Gets the lander name (recorded in summaries).</summary>
    string Name { get; }

    /// <summary>Lands the tasks.</summary>
    /// <param name="request">The request.</param>
    /// <returns>What landed.</returns>
    LandResult Land(LandRequest request);
}

/// <summary>Default lander until Plan B: fast-forwards the epic to the tested integration commit (one merge commit per task).</summary>
public sealed class FastForwardLander : ILander
{
    /// <inheritdoc/>
    public string Name => "fast-forward";

    /// <inheritdoc/>
    /// <exception cref="ToolException">The epic moved since the batch started (exit code 4).</exception>
    public LandResult Land(LandRequest request)
    {
        var r = request.Repo.Try("update-ref", "-m", $"batch {request.RunId}: land batch {request.Batch}", GitRunner.HeadsRef(request.EpicBranch), request.TestedCommit, request.EpicTipBefore);
        if (r.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Environment, $"epic branch '{request.EpicBranch}' moved during the run; nothing landed for batch {request.Batch}", "another writer updated the epic; re-run batch");
        }

        return new LandResult(request.TestedCommit, request.Tasks.Select(t => new LandedTask(t.Id, request.TestedCommit)).ToList(), null, []);
    }
}
