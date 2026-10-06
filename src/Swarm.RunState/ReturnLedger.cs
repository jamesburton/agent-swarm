namespace Swarm.RunState;

/// <summary>Writes <c>returned.jsonl</c> and tracks the latest record per task.</summary>
/// <param name="filePath">The ledger file.</param>
/// <param name="runId">Run id stamped on every record.</param>
public sealed class ReturnLedger(string filePath, string runId)
{
    readonly Dictionary<string, ReturnedEntry> latest = new(StringComparer.Ordinal);

    /// <summary>Gets the ledger file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Gets the latest record of every returned task.</summary>
    public IReadOnlyCollection<ReturnedEntry> Latest => latest.Values;

    /// <summary>Creates a record with empty lists, no rebase and final <c>pending</c> (not yet written).</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="branch">The worker's branch.</param>
    /// <param name="kind">A <see cref="ReturnKind"/> value.</param>
    /// <param name="stage">A <see cref="ReturnStage"/> value.</param>
    /// <param name="batch">Batch number.</param>
    /// <param name="reason">One-line reason.</param>
    /// <returns>The record.</returns>
    public static ReturnedEntry New(string taskId, string branch, string kind, string stage, int batch, string reason) =>
        new(SwarmJson.SchemaVersion, default, "", taskId, branch, kind, stage, batch, [], [], reason, "", RebaseState.NotApplicable, null, "", FinalState.Pending);

    /// <summary>Reads a ledger file and keeps the last record per task.</summary>
    /// <param name="filePath">The ledger file.</param>
    /// <returns>Task id to latest record.</returns>
    public static IReadOnlyDictionary<string, ReturnedEntry> ReadLatest(string filePath) =>
        JsonlFile.ReadAll<ReturnedEntry>(filePath).GroupBy(e => e.Task, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    /// <summary>Gets a task's latest record.</summary>
    /// <param name="taskId">Task id.</param>
    /// <returns>The record, or null.</returns>
    public ReturnedEntry? Get(string taskId) => latest.GetValueOrDefault(taskId);

    /// <summary>Gets the latest records waiting for a rebase.</summary>
    /// <returns>Records with <c>rebase: pending</c>.</returns>
    public IReadOnlyList<ReturnedEntry> Pending() => latest.Values.Where(e => e.Rebase == RebaseState.Pending).ToList();

    /// <summary>Stamps and appends a record; it becomes the task's latest.</summary>
    /// <param name="entry">The record.</param>
    /// <returns>The stamped record.</returns>
    public ReturnedEntry Record(ReturnedEntry entry)
    {
        var stamped = entry with { SchemaVersion = SwarmJson.SchemaVersion, Utc = DateTime.UtcNow, RunId = runId };
        JsonlFile.Append(FilePath, stamped);
        latest[stamped.Task] = stamped;
        return stamped;
    }

    /// <summary>Changes a task's latest record and appends the new snapshot.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="change">The change.</param>
    /// <returns>The stamped record.</returns>
    /// <exception cref="InvalidOperationException">The task has no record.</exception>
    public ReturnedEntry Update(string taskId, Func<ReturnedEntry, ReturnedEntry> change) =>
        Record(change(Get(taskId) ?? throw new InvalidOperationException($"task '{taskId}' has no returned record")));
}
