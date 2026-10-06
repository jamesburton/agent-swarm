using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>JSON conventions for every swarm tool output (camelCase, schema version 1, LF).</summary>
public static class SwarmJson
{
    /// <summary>Schema version written into every output object.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Gets single-line options (stdout lines, JSONL).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Gets indented options (files).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Serialises a value to one line.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>JSON without line breaks.</returns>
    public static string Line<T>(T value) => JsonSerializer.Serialize(value, Compact);

    /// <summary>Writes an indented JSON file with LF endings, replacing any existing file atomically.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="path">Target file.</param>
    /// <param name="value">The value.</param>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.tmp-{Environment.ProcessId}";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Indented) + "\n");
        SharedFile.Retry(() => File.Move(temp, path, overwrite: true));
    }

    /// <summary>Reads a JSON file.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The file holds JSON null.</exception>
    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Compact) ?? throw new InvalidDataException($"{path} holds no value");

    static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web) { WriteIndented = indented, NewLine = "\n" };
}
