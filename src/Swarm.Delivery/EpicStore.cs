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

    /// <summary>Reads every record; a file that cannot be read is reported on its own and never hides the others.</summary>
    /// <returns>Records ordered by id, and the unreadable files ordered by path.</returns>
    public EpicListing All()
    {
        var records = new List<EpicRecord>();
        var unreadable = new List<EpicFileError>();
        foreach (var file in Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.json") : [])
        {
            try
            {
                if (Find(Path.GetFileNameWithoutExtension(file)) is { } record)
                {
                    records.Add(record);
                }
                else
                {
                    unreadable.Add(new EpicFileError(file, "empty record (or deleted while listing)"));
                }
            }
            catch (Exception e) when (e is ToolException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new EpicFileError(file, TextLines.OneLine(e.Message)));
            }
        }

        return new EpicListing(
            records.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            unreadable.OrderBy(u => u.Path, StringComparer.Ordinal).ToList());
    }
}

/// <summary>A file in the epic records directory that could not be read as a record.</summary>
/// <param name="Path">The file.</param>
/// <param name="Error">One-line reason (unsafe file name, invalid JSON, I/O failure).</param>
public sealed record EpicFileError(string Path, string Error);

/// <summary>Result of <see cref="EpicStore.All"/>.</summary>
/// <param name="Records">Readable records, ordered by id.</param>
/// <param name="Unreadable">Files that could not be read, ordered by path.</param>
public sealed record EpicListing(IReadOnlyList<EpicRecord> Records, IReadOnlyList<EpicFileError> Unreadable);
