using System.Text.Json;
using System.Text.Json.Serialization;
using Swarm.Git;

namespace Swarm.Batching;

/// <summary>One task in queue order.</summary>
/// <param name="Id">Task id (a safe name).</param>
/// <param name="Branch">Branch to land (the worker's branch, or a rebased copy).</param>
/// <param name="DependsOn">Ids of earlier tasks this task is stacked on.</param>
public sealed record TaskSpec(string Id, string Branch, IReadOnlyList<string> DependsOn)
{
    /// <summary>Gets the full ref of <see cref="Branch"/>.</summary>
    public string BranchRef => "refs/heads/" + Branch;
}

/// <summary>Reads and validates a tasks file.</summary>
public static class TasksFile
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly string[] BadBranchParts = ["..", "~", "^", ":", "?", "*", "[", "\\", "@{"];

    /// <summary>Loads a tasks file.</summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>Tasks in queue order.</returns>
    /// <exception cref="ToolException">Missing or invalid file (exit code 3).</exception>
    public static IReadOnlyList<TaskSpec> Load(string path) =>
        File.Exists(path)
            ? Parse(File.ReadAllText(path), path)
            : throw new ToolException(ExitCodes.BadInput, $"tasks file '{path}' not found");

    /// <summary>Parses tasks JSON.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns>Tasks in queue order.</returns>
    /// <exception cref="ToolException">Invalid content (exit code 3).</exception>
    public static IReadOnlyList<TaskSpec> Parse(string json, string sourceName)
    {
        List<TaskDto?> raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<TaskDto?>>(json, Options) ?? throw Bad(sourceName, "invalid JSON: expected an array of tasks");
        }
        catch (JsonException e)
        {
            throw Bad(sourceName, "invalid JSON: " + e.Message);
        }

        var tasks = new List<TaskSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            var dto = raw[i] ?? throw Bad(sourceName, $"task #{i + 1}: must be an object");
            var id = dto.Id ?? throw Bad(sourceName, $"task #{i + 1}: missing 'id'");
            if (!SafeName.IsValid(id))
            {
                throw Bad(sourceName, $"task id '{id}' is not a safe name ({SafeName.Description})");
            }

            if (!seen.Add(id))
            {
                throw Bad(sourceName, $"duplicate task id '{id}' (ids are compared case-insensitively)");
            }

            var branch = dto.Branch ?? throw Bad(sourceName, $"task '{id}': missing 'branch'");
            if (!IsPlausibleBranch(branch))
            {
                throw Bad(sourceName, $"task '{id}': branch '{branch}' is not a valid branch name");
            }

            var deps = dto.DependsOn ?? new List<string>();
            foreach (var dep in deps)
            {
                if (!tasks.Any(t => string.Equals(t.Id, dep, StringComparison.Ordinal)))
                {
                    throw Bad(sourceName, $"task '{id}': dependsOn '{dep}' must name an earlier task in the file");
                }
            }

            tasks.Add(new TaskSpec(id, branch, deps));
        }

        return tasks;
    }

    static bool IsPlausibleBranch(string b) =>
        b.Length > 0
        && !b.StartsWith('-')
        && !b.EndsWith('/')
        && !b.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !b.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && !BadBranchParts.Any(p => b.Contains(p, StringComparison.Ordinal));

    static ToolException Bad(string source, string message) => new(ExitCodes.BadInput, $"{source}: {message}", "see docs/batch-tools.md#tasks-file");

    sealed class TaskDto
    {
        public string? Id { get; set; }

        public string? Branch { get; set; }

        public List<string>? DependsOn { get; set; }

        // Accepted for compatibility and ignored: touches are derived from git.
        public JsonElement? Touches { get; set; }

        public string? Title { get; set; }
    }
}
