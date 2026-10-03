using Swarm.Core;

namespace Swarm.Render;

/// <summary>Renders every file of a swarm: the agent files, then the workflow scripts and the runbook.</summary>
public static class SwarmRenderer
{
    /// <summary>Renders all files in memory (nothing is written).</summary>
    /// <param name="s">The swarm definition.</param>
    /// <returns>Relative path to file content, agent files first, then the workflow files, each in their renderer's order.</returns>
    /// <exception cref="SwarmException">Thrown with the first rendering error.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> Render(SwarmDefinition s) =>
        [.. AgentFileRenderer.Render(s), .. WorkflowRenderer.Render(s)];
}
