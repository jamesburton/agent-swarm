using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Squashing;

/// <summary>Chooses the lander named by <see cref="SwarmConfig.Lander"/>.</summary>
public static class Landers
{
    /// <summary>Creates the configured lander.</summary>
    /// <param name="config">Validated config.</param>
    /// <returns><see cref="SquashLander"/> for <c>squash</c>, <see cref="FastForwardLander"/> for <c>fast-forward</c>.</returns>
    /// <exception cref="ToolException">Unknown lander name (exit code 2).</exception>
    public static ILander Create(SwarmConfig config) => config.Lander switch
    {
        LanderNames.Squash => new SquashLander(config.Squash, config.BaseBranch),
        LanderNames.FastForward => new FastForwardLander(),
        _ => throw new ToolException(ExitCodes.Usage, $"unknown lander '{config.Lander}'", "use 'squash' or 'fast-forward' in .swarm/batch.json"),
    };
}
