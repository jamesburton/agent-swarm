using System.Text.Json;

namespace Swarm.Tools.Tests.Support;

/// <summary>Helpers for asserting on CLI stdout.</summary>
public static class JsonOutput
{
    /// <summary>Parses stdout that must consist of exactly one JSON line.</summary>
    /// <param name="stdout">The captured standard output.</param>
    /// <returns>The root element of the single line.</returns>
    public static JsonElement SingleJsonLine(string stdout) =>
        JsonDocument.Parse(Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).RootElement;
}
