using Swarm.Batching;

namespace Swarm.Tools.Tests.Support;

/// <summary>Shared task builders for batching tests.</summary>
public static class Tasks
{
    /// <summary>Builds a task whose branch is <c>task/&lt;id&gt;</c>.</summary>
    /// <param name="id">Task id.</param>
    /// <param name="deps">Ids of earlier tasks it is stacked on.</param>
    /// <returns>The task.</returns>
    public static TaskSpec T(string id, params string[] deps) => new(id, "task/" + id, deps);
}
