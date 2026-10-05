using System.Text.Json;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Epic lifecycle states.</summary>
public static class EpicStates
{
    /// <summary>Branch exists, work is landing.</summary>
    public const string Open = "open";

    /// <summary>Merged into the active branch.</summary>
    public const string Closed = "closed";
}

/// <summary>One epic, stored as <c>&lt;state&gt;/epics/&lt;id&gt;.json</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Id">Epic id (a safe name).</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="BaseBranch">Active branch it was opened from and closes into by default.</param>
/// <param name="BaseCommit">Base commit at open.</param>
/// <param name="CreatedUtc">When it was opened.</param>
/// <param name="State">An <see cref="EpicStates"/> value.</param>
/// <param name="ClosedUtc">When it was closed.</param>
/// <param name="MergeCommit">The <c>--no-ff</c> merge commit on the active branch.</param>
/// <param name="MergedInto">The branch it was merged into.</param>
public sealed record EpicRecord(
    int SchemaVersion, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, DateTime CreatedUtc,
    string State, DateTime? ClosedUtc, string? MergeCommit, string? MergedInto);

/// <summary>Epic records in the run-state directory.</summary>
/// <param name="state">State layout.</param>
public sealed class EpicStore(StateLayout state)
{
    /// <summary>Gets the records directory.</summary>
    public string Dir { get; } = Path.Combine(state.Root, "epics");

    /// <summary>Gets a record's file path.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ToolException">Unsafe id (exit code 2).</exception>
    public string PathOf(string id) =>
        SafeName.IsValid(id) ? Path.Combine(Dir, id + ".json") : throw new ToolException(ExitCodes.Usage, $"epic id '{id}' is not a safe name ({SafeName.Description})");

    /// <summary>Reads a record if it exists.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The record, or null.</returns>
    /// <exception cref="ToolException">Unsafe id (2) or unreadable file (3).</exception>
    public EpicRecord? Find(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return SwarmJson.Read<EpicRecord>(path);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new ToolException(ExitCodes.BadInput, $"epic file '{path}' is not valid: {e.Message}", "fix or delete it");
        }
    }

    /// <summary>Reads a record that must exist.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ToolException">Unknown epic (exit code 3).</exception>
    public EpicRecord Get(string id) =>
        Find(id) ?? throw new ToolException(ExitCodes.BadInput, $"epic '{id}' not found in {Dir}", $"open it first: epic open {id} <slug>");

    /// <summary>Writes a record (atomic replace).</summary>
    /// <param name="record">The record.</param>
    public void Save(EpicRecord record) => SwarmJson.WriteFile(PathOf(record.Id), record);

    /// <summary>Reads every record.</summary>
    /// <returns>Records ordered by id.</returns>
    public IReadOnlyList<EpicRecord> All() =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*.json").Select(f => Get(Path.GetFileNameWithoutExtension(f))).OrderBy(r => r.Id, StringComparer.Ordinal).ToList()
            : [];
}
