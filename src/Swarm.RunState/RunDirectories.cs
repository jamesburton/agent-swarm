using System.Globalization;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Creates and prunes per-run folders under <c>&lt;state&gt;/runs</c>.</summary>
public static class RunDirectories
{
    /// <summary>File whose presence marks a finished run.</summary>
    public const string SummaryFileName = "summary.json";

    /// <summary>Builds a timestamped run id.</summary>
    /// <param name="epic">Epic id.</param>
    /// <param name="utc">Start time.</param>
    /// <returns><c>yyyyMMdd-HHmmss-fff-&lt;epic&gt;</c>.</returns>
    public static string NewRunId(string epic, DateTime utc) => utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + epic;

    /// <summary>Creates a run folder with a <c>logs</c> subfolder.</summary>
    /// <param name="layout">State layout.</param>
    /// <param name="runId">Run id.</param>
    /// <returns>The run folder.</returns>
    /// <exception cref="ToolException">Unsafe or existing id (exit code 2).</exception>
    public static string Create(StateLayout layout, string runId)
    {
        if (!SafeName.IsValid(runId))
        {
            throw new ToolException(ExitCodes.Usage, $"run id '{runId}' is not valid ({SafeName.Description})");
        }

        var dir = layout.RunDir(runId);
        if (Directory.Exists(dir))
        {
            throw new ToolException(ExitCodes.Usage, $"run id '{runId}' already exists in {layout.RunsDir}", "choose another --run-id");
        }

        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        return dir;
    }

    /// <summary>Deletes finished runs beyond the newest <paramref name="keep"/>; unfinished runs are never deleted.</summary>
    /// <param name="layout">State layout.</param>
    /// <param name="keep">Finished runs to keep.</param>
    /// <returns>Names of deleted runs.</returns>
    public static IReadOnlyList<string> Prune(StateLayout layout, int keep)
    {
        if (!Directory.Exists(layout.RunsDir))
        {
            return [];
        }

        var old = new DirectoryInfo(layout.RunsDir).GetDirectories()
            .Select(d => (Dir: d, Summary: Path.Combine(d.FullName, SummaryFileName)))
            .Where(x => File.Exists(x.Summary))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Summary))
            .Skip(keep)
            .Select(x => x.Dir)
            .ToList();
        foreach (var dir in old)
        {
            FileTree.DeleteTree(dir.FullName);
        }

        return old.Select(d => d.Name).ToList();
    }
}
