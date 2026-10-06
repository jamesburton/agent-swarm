namespace Swarm.RunState;

/// <summary>Event type names written to <c>events.jsonl</c>.</summary>
public static class EventTypes
{
    /// <summary>A batch run started.</summary>
    public const string RunStart = "run-start";

    /// <summary>A top-level batch started.</summary>
    public const string BatchStart = "batch-start";

    /// <summary>A set was merged into the integration worktree.</summary>
    public const string Merge = "merge";

    /// <summary>A task conflicted.</summary>
    public const string Conflict = "conflict";

    /// <summary>A suite ran.</summary>
    public const string Suite = "suite";

    /// <summary>A red set was split.</summary>
    public const string Bisect = "bisect";

    /// <summary>A task landed.</summary>
    public const string Land = "land";

    /// <summary>A returned task was rebased.</summary>
    public const string Rebase = "rebase";

    /// <summary>Tasks went back to the queue.</summary>
    public const string Requeue = "requeue";

    /// <summary>A batch run ended.</summary>
    public const string RunEnd = "run-end";

    /// <summary>A testgate run finished.</summary>
    public const string Gate = "gate";
}

/// <summary>One line of an event log.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Utc">When it happened.</param>
/// <param name="RunId">Run id (or <c>testgate</c>).</param>
/// <param name="Type">An <see cref="EventTypes"/> value.</param>
/// <param name="Data">Event-specific payload.</param>
public sealed record RunEvent(int SchemaVersion, DateTime Utc, string RunId, string Type, object? Data);

/// <summary>Appends <see cref="RunEvent"/> lines to a JSONL file.</summary>
/// <param name="filePath">The events file.</param>
/// <param name="runId">Run id stamped on every event.</param>
public sealed class EventLog(string filePath, string runId)
{
    /// <summary>Gets the events file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Appends one event.</summary>
    /// <param name="type">An <see cref="EventTypes"/> value.</param>
    /// <param name="data">Payload (serialised with its runtime type).</param>
    public void Write(string type, object? data = null) =>
        JsonlFile.Append(FilePath, new RunEvent(SwarmJson.SchemaVersion, DateTime.UtcNow, runId, type, data));
}
