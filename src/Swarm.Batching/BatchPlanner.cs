namespace Swarm.Batching;

/// <summary>Pure batch planning: adaptive size, pre-batching, bisect halves.</summary>
public static class BatchPlanner
{
    /// <summary>Adapts the batch size: doubles after green, halves after red, within bounds.</summary>
    /// <param name="current">Current size.</param>
    /// <param name="red">Whether the batch had any red.</param>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <returns>The next size.</returns>
    public static int NextSize(int current, bool red, int min, int max) =>
        red ? Math.Max(current / 2, min) : Math.Min(current * 2, max);

    /// <summary>Picks the next batch from the queue.</summary>
    /// <param name="queue">Units in queue order.</param>
    /// <param name="size">Target number of tasks.</param>
    /// <param name="overlaps">Whether two units change a common file.</param>
    /// <param name="prebatch">Whether overlapping units are kept apart.</param>
    /// <returns>The picked units (never empty for a non-empty queue: the head is always taken).</returns>
    public static IReadOnlyList<TaskUnit> PreBatch(IReadOnlyList<TaskUnit> queue, int size, Func<TaskUnit, TaskUnit, bool> overlaps, bool prebatch)
    {
        var pick = new List<TaskUnit>();
        var taken = 0;
        foreach (var unit in queue)
        {
            if (pick.Count > 0 && taken + unit.Size > size)
            {
                continue;
            }

            if (prebatch && pick.Any(p => overlaps(p, unit)))
            {
                continue;
            }

            pick.Add(unit);
            taken += unit.Size;
            if (taken >= size)
            {
                break;
            }
        }

        return pick;
    }

    /// <summary>Splits a red set for bisect (units are never split).</summary>
    /// <param name="units">At least two units.</param>
    /// <returns>Left (the first Count/2 units) and right.</returns>
    public static (IReadOnlyList<TaskUnit> Left, IReadOnlyList<TaskUnit> Right) Halve(IReadOnlyList<TaskUnit> units)
    {
        var half = units.Count / 2;
        return (units.Take(half).ToList(), units.Skip(half).ToList());
    }
}
