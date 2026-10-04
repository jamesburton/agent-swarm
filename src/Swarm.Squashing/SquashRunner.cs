using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Squashing;

/// <summary>One manual squash.</summary>
/// <param name="TaskId">Task id (a safe name).</param>
/// <param name="Branch">Task branch (never modified).</param>
/// <param name="Ticket">Explicit ticket, or null to derive it.</param>
/// <param name="RunId">Run id, or null for <c>squash-&lt;timestamp&gt;-&lt;epic&gt;</c>.</param>
public sealed record SquashRunRequest(string TaskId, string Branch, string? Ticket, string? RunId);

/// <summary>The single stdout line of <c>squash run</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="RunId">Run id (the <c>Swarm-Run:</c> trailer).</param>
/// <param name="Task">Task id.</param>
/// <param name="Branch">Task branch.</param>
/// <param name="Epic">Epic id.</param>
/// <param name="EpicBranch">Epic branch.</param>
/// <param name="EpicTipBefore">Epic tip before the run.</param>
/// <param name="EpicTipAfter">Epic tip after the run.</param>
/// <param name="Ticket">The ticket used, or null when none was found.</param>
/// <param name="Commit">The squashed commit, or null when nothing landed.</param>
/// <param name="Empty">True when the epic already had the change (no commit made).</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Note">Why nothing landed, or null.</param>
public sealed record SquashRunResult(
    int SchemaVersion, string RunId, string Task, string Branch, string Epic, string EpicBranch, string EpicTipBefore, string EpicTipAfter,
    string? Ticket, string? Commit, bool Empty, int ExitCode, string? Note);

/// <summary>Squashes one task branch onto its epic: same lock, integration worktree, chain and lander as <c>batch</c>.</summary>
/// <param name="context">Resolved repo, config and state.</param>
/// <param name="progress">Human progress (stderr).</param>
public sealed class SquashRunner(ToolContext context, Progress progress)
{
    /// <summary>Batch number stamped on manual squashes.</summary>
    public const int ManualBatch = 0;

    /// <summary>Gets a test hook run right after the lander returns (simulates a concurrent epic move).</summary>
    internal Action? AfterLand { get; init; }

    /// <summary>Runs the squash.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The result (exit 0 landed or empty, 1 conflict or land failure).</returns>
    /// <exception cref="ToolException">Bad values or paths (2), missing branch or epic (3), epic lock held, git or epic moved (4).</exception>
    public SquashRunResult Run(SquashRunRequest request)
    {
        var config = context.Config;
        if (!SafeName.IsValid(request.TaskId))
        {
            throw new ToolException(ExitCodes.Usage, $"--task '{request.TaskId}' is not a safe name ({SafeName.Description})");
        }

        if (request.Ticket is { } t && (t.Length == 0 || t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new ToolException(ExitCodes.Usage, $"--ticket '{t}' must be one token without spaces");
        }

        var runId = request.RunId ?? "squash-" + RunDirectories.NewRunId(config.Epic, DateTime.UtcNow);
        if (!SafeName.IsValid(runId))
        {
            throw new ToolException(ExitCodes.Usage, $"run id '{runId}' is not valid ({SafeName.Description})");
        }

        var main = new GitRunner(context.Repo.MainWorktreeRoot);
        if (main.Try("check-ref-format", "--branch", request.Branch).ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Usage, $"--branch '{request.Branch}' is not a valid branch name");
        }

        var worktreePath = StatePaths.Guard(Path.Combine(StatePaths.ResolveWorktreeRoot(context.Repo, config.WorktreeRoot), "int-" + config.Epic), "integration worktree");
        if (!main.RefExists(GitRunner.HeadsRef(request.Branch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"task branch '{request.Branch}' not found");
        }

        RepoChecks.EnsureEpic(main, config.EpicBranch);

        // The same per-epic lock as batch: both use the integration worktree and move the epic.
        var lockDir = context.State.BatchLockDir(config.Epic);
        using var epicLock = new SlotSemaphore(lockDir, SlotOptions.From(config) with { Slots = 1, MaxWait = null }).TryAcquire($"squash run {runId}")
            ?? throw new ToolException(ExitCodes.Environment, $"another batch or squash run holds epic '{config.Epic}'", $"wait for it to finish; lock in {lockDir}");
        var tip = main.RevParse(GitRunner.HeadsRef(config.EpicBranch));
        var worktree = new IntegrationWorktree(main, worktreePath);
        worktree.Ensure(config.EpicBranch);
        var result = new SquashRunResult(SwarmJson.SchemaVersion, runId, request.TaskId, request.Branch, config.Epic, config.EpicBranch, tip, tip, null, null, false, ExitCodes.Ok, null);
        try
        {
            var spec = new TaskSpec(request.TaskId, request.Branch, []);
            var integration = worktree.Integrate(tip, TaskUnits.Build([spec]));
            if (integration.Conflicts.Count > 0)
            {
                var files = string.Join(',', integration.Conflicts[0].Files);
                progress.Info($"CONFLICT {request.TaskId} vs the epic tip in {files}: nothing landed");
                progress.Detail(integration.Conflicts[0].GitOutput);
                return result with { ExitCode = ExitCodes.Returned, Note = $"merge conflict with the epic tip in {files}" };
            }

            var land = new LandRequest(main, worktree.Git, config.Epic, config.EpicBranch, tip, integration.Head, [new LandTask(spec.Id, spec.Branch, [])], ManualBatch, runId);
            var outcome = new SquashLander(config.Squash, config.BaseBranch, request.Ticket).Execute(land);
            AfterLand?.Invoke();
            if (outcome.Result.Failure is { } failure)
            {
                progress.Info($"RETURN {request.TaskId}: {failure.GitOutput}");
                return result with { ExitCode = ExitCodes.Returned, Note = failure.GitOutput };
            }

            var squash = outcome.Commits.Single();
            if (squash.Empty && main.RevParse(GitRunner.HeadsRef(config.EpicBranch)) != tip)
            {
                // An empty land makes no update-ref (no compare-and-swap), so a concurrent epic move would go unnoticed.
                throw new ToolException(ExitCodes.Environment, $"epic '{config.EpicBranch}' moved during the run");
            }

            progress.Info(squash.Empty
                ? $"{request.TaskId}: nothing to land, '{config.EpicBranch}' already has this change"
                : $"{request.TaskId}: landed {squash.Commit} on '{config.EpicBranch}': {squash.Subject}");
            return result with
            {
                EpicTipAfter = outcome.Result.EpicTipAfter,
                Ticket = squash.Ticket,
                Commit = squash.Commit,
                Empty = squash.Empty,
                Note = squash.Empty ? "nothing to land: the epic already has this change" : null,
            };
        }
        finally
        {
            worktree.ResetTo(GitRunner.HeadsRef(config.EpicBranch));
        }
    }
}
