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
                // ReadWrite access (not Append mode) lets us inspect the last byte inside the same exclusive open.
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    var last = stream.ReadByte();

                    // A crash mid-append left a fragment: terminate it so this record starts on its own line.
                    if (last != '\n')
                    {
                        stream.WriteByte((byte)'\n');
                    }
                }
                else
                {
                    stream.Seek(0, SeekOrigin.End);
                }

                stream.Write(bytes);
            },
            attempts: 2000,
            delayMs: 2);
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
        var records = new List<T>();
        foreach (var line in lines[..^1].Where(l => l.Trim().Length > 0))
        {
            // Malformed or null lines are crash fragments; schema problems are not skipped.
            try
            {
                if (JsonSerializer.Deserialize<T>(line, SwarmJson.Compact) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // Skip the fragment.
            }
        }

        return records;
    }
}
