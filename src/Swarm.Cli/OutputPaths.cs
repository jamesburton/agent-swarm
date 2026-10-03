using System.Text;

namespace Swarm.Cli;

/// <summary>Safe writing of rendered files beneath an output directory.</summary>
public static class OutputPaths
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Resolves a rendered relative key to a full path guaranteed to lie inside <paramref name="outDir"/>.</summary>
    /// <param name="outDir">The output directory.</param>
    /// <param name="key">A relative, forward-slash path produced by a renderer.</param>
    /// <returns>The absolute destination path.</returns>
    /// <exception cref="InvalidDataException">Thrown when the key is empty, rooted, drive-qualified or escapes the directory.</exception>
    public static string Resolve(string outDir, string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || Path.IsPathRooted(key)
            || key.Contains(':')
            || key.Split('/', '\\').Contains(".."))
        {
            throw new InvalidDataException($"refusing to write outside the output directory: '{key}'");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        var full = Path.GetFullPath(Path.Combine(root, key));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, cmp))
        {
            throw new InvalidDataException($"refusing to write outside the output directory: '{key}'");
        }

        return full;
    }

    /// <summary>Writes all files (UTF-8, no BOM), overwriting existing ones and never deleting; every path is verified before anything is written.</summary>
    /// <param name="outDir">The output directory (created when absent).</param>
    /// <param name="files">Relative path to content.</param>
    /// <returns>The relative paths written, in input order.</returns>
    /// <exception cref="InvalidDataException">Thrown, before any write, when a path escapes <paramref name="outDir"/>.</exception>
    public static IReadOnlyList<string> WriteAll(string outDir, IEnumerable<KeyValuePair<string, string>> files)
    {
        var plan = files.Select(f => (f.Key, Path: Resolve(outDir, f.Key), f.Value)).ToList();
        foreach (var (_, path, content) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8NoBom);
        }

        return plan.Select(p => p.Key).ToList();
    }
}
