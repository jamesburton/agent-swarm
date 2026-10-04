using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

public static class BatchScenario
{
    public static BatchSummary Run(TempRepo repo, string tasksFile, ILander? lander = null, Func<SwarmConfig, SwarmConfig>? tweak = null, bool? prebatch = null, string mode = BatchModes.Batched)
    {
        var config = TestConfig.For(repo);
        config = tweak?.Invoke(config) ?? config;
        var options = new BatchRunOptions { TasksFile = tasksFile, Config = config, Prebatch = prebatch, Mode = mode };
        return new BatchEngine(RepoLocator.Locate(repo.Root), options, lander ?? new FastForwardLander(), new Progress(TextWriter.Null, Verbosity.Quiet)).Run();
    }

    public static IReadOnlyDictionary<string, ReturnedEntry> Returned(BatchSummary s) => ReturnLedger.ReadLatest(s.ReturnedFile);
}

/// <summary>
/// Lands like <see cref="FastForwardLander"/>, except that the first <c>failures</c> requests containing
/// <c>failTaskId</c> fail at that task.
/// </summary>
public sealed class FailOnceLander(string failTaskId, int failures = 1) : ILander
{
    int failed;

    public string Name => "fail-once";

    public LandResult Land(LandRequest r)
    {
        var index = r.Tasks.ToList().FindIndex(t => t.Id == failTaskId);
        if (failed >= failures || index < 0)
        {
            return new FastForwardLander().Land(r);
        }

        failed++;

        // The integration chain is first-parent: TestedCommit~k drops the last k task merges.
        var prefix = r.Repo.RevParse($"{r.TestedCommit}~{r.Tasks.Count - index}");
        if (prefix != r.EpicTipBefore)
        {
            r.Repo.Run("update-ref", GitRunner.HeadsRef(r.EpicBranch), prefix, r.EpicTipBefore);
        }

        return new LandResult(
            prefix,
            r.Tasks.Take(index).Select(t => new LandedTask(t.Id, prefix)).ToList(),
            new LandFailure(failTaskId, [], "simulated land failure"),
            r.Tasks.Skip(index + 1).Select(t => t.Id).ToList());
    }
}

/// <summary>Violates the lander contract: lands one commit short of the tested tree.</summary>
public sealed class ShortLander : ILander
{
    public string Name => "short";

    public LandResult Land(LandRequest r)
    {
        var target = r.Repo.RevParse(r.TestedCommit + "~1");
        if (target != r.EpicTipBefore)
        {
            r.Repo.Run("update-ref", GitRunner.HeadsRef(r.EpicBranch), target, r.EpicTipBefore);
        }

        return new LandResult(target, r.Tasks.Select(t => new LandedTask(t.Id, target)).ToList(), null, []);
    }
}

/// <summary>Throws a non-<see cref="ToolException"/> from <see cref="Land"/>, like a buggy lander.</summary>
public sealed class ThrowingLander : ILander
{
    public string Name => "throwing";

    public LandResult Land(LandRequest r) => throw new InvalidOperationException("boom");
}

/// <summary>Violates the lander contract: lands nothing and reports every task not attempted without a failure.</summary>
public sealed class NothingAttemptedLander : ILander
{
    public string Name => "nothing-attempted";

    public LandResult Land(LandRequest r) => new(r.EpicTipBefore, [], null, r.Tasks.Select(t => t.Id).ToList());
}
