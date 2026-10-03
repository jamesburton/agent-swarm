using System.Text;
using Swarm.Render;

namespace Swarm.Cli;

/// <summary>One file written by <see cref="OutputPaths.WriteAll"/>.</summary>
/// <param name="Path">The relative path, as the renderer produced it.</param>
/// <param name="Overwrote">True when a file already existed at that path and was replaced.</param>
public sealed record WrittenFile(string Path, bool Overwrote);

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

        // Normalise to exactly one trailing separator; a filesystem root (C:\ or /) already has one.
        var root = Path.GetFullPath(outDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        var full = Path.GetFullPath(Path.Combine(root, key));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, cmp))
        {
            throw new InvalidDataException($"refusing to write outside the output directory: '{key}'");
        }

        return full;
    }

    /// <summary>
    /// Writes all files (UTF-8, no BOM) and never deletes. Before anything is written, every path is verified to stay inside
    /// <paramref name="outDir"/>, and every existing file is checked for the <see cref="GeneratedMarker"/>: a file without it
    /// (hand-written, or from another tool) is only overwritten when <paramref name="force"/> is true.
    /// </summary>
    /// <remarks>
    /// The renderers run entirely in memory beforehand, so the only remaining failures are disk or permission errors, which can leave earlier files already written (there is no rollback).
    /// Known limits: symlinks and junctions inside <c>outDir</c> are not resolved, and reserved device names are not rejected (keys come from the renderers).
    /// </remarks>
    /// <param name="outDir">The output directory (created when absent).</param>
    /// <param name="files">Relative path to content.</param>
    /// <param name="force">Overwrite existing files even when they lack the generated marker.</param>
    /// <returns>The files written, in input order, each marked as new or overwritten.</returns>
    /// <exception cref="InvalidDataException">Thrown, before any write, when a path escapes <paramref name="outDir"/>.</exception>
    /// <exception cref="IOException">Thrown, before any write, when an existing file lacks the generated marker and <paramref name="force"/> is false.</exception>
    public static IReadOnlyList<WrittenFile> WriteAll(string outDir, IEnumerable<KeyValuePair<string, string>> files, bool force = false)
    {
        var plan = files.Select(f => (f.Key, Path: Resolve(outDir, f.Key), f.Value)).Select(p => (p.Key, p.Path, p.Value, Exists: File.Exists(p.Path))).ToList();
        if (!force && plan.FirstOrDefault(p => p.Exists && !GeneratedMarker.IsPresent(File.ReadAllText(p.Path))) is { Key: { } mine })
        {
            throw new IOException($"refusing to overwrite '{mine}': it has no swarm:generated marker (it was not written by swarm render); use --force to overwrite it");
        }

        foreach (var (_, path, content, _) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8NoBom);
        }

        return plan.Select(p => new WrittenFile(p.Key, p.Exists)).ToList();
    }
}
