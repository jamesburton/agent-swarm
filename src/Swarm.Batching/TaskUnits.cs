namespace Swarm.Batching;

/// <summary>Tasks that land together: a single task, or a stack connected by <c>dependsOn</c>.</summary>
/// <param name="Members">Members in queue order.</param>
public sealed record TaskUnit(IReadOnlyList<TaskSpec> Members)
{
    /// <summary>Gets the first member's id.</summary>
    public string Id => Members[0].Id;

    /// <summary>Gets the number of tasks.</summary>
    public int Size => Members.Count;

    /// <summary>Gets the member ids in order.</summary>
    public IEnumerable<string> Ids => Members.Select(m => m.Id);
}

/// <summary>Builds land units from tasks.</summary>
public static class TaskUnits
{
    /// <summary>Groups tasks connected by <c>dependsOn</c> (ids not in the list are ignored).</summary>
    /// <param name="tasks">Tasks in queue order.</param>
    /// <returns>Units ordered by their first member's position.</returns>
    public static IReadOnlyList<TaskUnit> Build(IReadOnlyList<TaskSpec> tasks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            index[tasks[i].Id] = i;
        }

        // Union-find whose roots are always the smallest index in the set.
        var parent = Enumerable.Range(0, tasks.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        for (var i = 0; i < tasks.Count; i++)
        {
            foreach (var dep in tasks[i].DependsOn)
            {
                if (index.TryGetValue(dep, out var j))
                {
                    var (a, b) = (Find(i), Find(j));
                    parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
        }

        return Enumerable.Range(0, tasks.Count)
            .GroupBy(Find)
            .OrderBy(g => g.Key)
            .Select(g => new TaskUnit(g.Order().Select(i => tasks[i]).ToList()))
            .ToList();
    }
}
