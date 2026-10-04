using System.Text;
using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Append-only JSON-lines files shared by several processes.</summary>
public static class JsonlFile
{
    /// <summary>Appends one record as one line.</summary>
    /// <typeparam name="T">Record type.</typeparam>
    /// <param name="path">The file (created with its directory).</param>
    /// <param name="record">The record.</param>
    public static void Append<T>(string path, T record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = Encoding.UTF8.GetBytes(SwarmJson.Line(record) + "\n");

        // FileShare.Read admits one writer at a time: a concurrent writer gets a sharing violation and retries.
        SharedFile.Retry(
            () =>
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            },
            attempts: 400,
            delayMs: 10);
    }

    /// <summary>Reads every complete line.</summary>
    /// <typeparam name="T">Record type.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>Records in file order; empty when the file is missing.</returns>
    public static IReadOnlyList<T> ReadAll<T>(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        // The element after the last '\n' is empty, or a line still being written: skip it either way.
        var lines = text.Split('\n');
        return lines[..^1].Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<T>(l, SwarmJson.Compact)!).ToList();
    }
}
