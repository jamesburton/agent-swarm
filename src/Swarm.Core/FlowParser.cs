namespace Swarm.Core;

/// <summary>Parses flow stage entries (<c>name*</c>, <c>name</c>, <c>gate:name</c>, <c>tool:name</c>).</summary>
public static class FlowParser
{
    /// <summary>Parses each entry in order.</summary>
    /// <param name="entries">The raw stage entries.</param>
    /// <returns>The parsed stages.</returns>
    /// <exception cref="SwarmException">Thrown when an entry is empty or invalid.</exception>
    public static IReadOnlyList<Stage> Parse(IEnumerable<string> entries) => [.. entries.Select(ParseEntry)];

    /// <summary>Parses a single entry.</summary>
    /// <param name="entry">The raw entry (surrounding whitespace is ignored).</param>
    /// <returns>The parsed stage.</returns>
    /// <exception cref="SwarmException">Thrown when the entry is empty or invalid.</exception>
    public static Stage ParseEntry(string entry)
    {
        var e = (entry ?? "").Trim();
        if (e.Length == 0) throw new SwarmException("empty flow entry");
        var (type, target) =
            e.StartsWith("gate:", StringComparison.Ordinal) ? (StageType.Gate, e[5..]) :
            e.StartsWith("tool:", StringComparison.Ordinal) ? (StageType.Tool, e[5..]) :
            e.EndsWith('*') ? (StageType.Fanout, e[..^1]) : (StageType.Role, e);
        target = target.Trim();
        if (target.Length == 0 || target.Any(c => char.IsWhiteSpace(c) || c == '*' || c == ':'))
            throw new SwarmException($"invalid flow entry '{e}'");
        return new Stage(type, target);
    }
}
