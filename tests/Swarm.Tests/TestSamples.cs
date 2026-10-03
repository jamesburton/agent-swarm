using Swarm.Core;
using Swarm.Formats;

/// <summary>Shared sample definitions copied to the test output directory.</summary>
public static class TestSamples
{
    static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", file));

    /// <summary>Returns the sample Markdown definition text.</summary>
    /// <returns>The file contents.</returns>
    public static string Markdown() => Read("epic-delivery.md");

    /// <summary>Returns the sample YAML definition text.</summary>
    /// <returns>The file contents.</returns>
    public static string Yaml() => Read("epic-delivery.yaml");

    /// <summary>Returns the sample Markdown definition parsed and validated.</summary>
    /// <returns>The parsed definition.</returns>
    public static SwarmDefinition Parsed() => MarkdownFrontEnd.Parse(Markdown());
}
