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

    /// <summary>Flattens a definition into one comparable string (every field of every role, tool, gate and stage).</summary>
    /// <param name="s">The definition.</param>
    /// <returns>The shape text; equal shapes mean equal definitions.</returns>
    public static string Shape(SwarmDefinition s) => string.Join("|",
        [s.Name, s.Description,
         .. s.Roles.Select(r => $"R:{r.Name},{r.Kind},{r.Model},{r.Description},{string.Join(';', r.Tools)},{r.MaxTurns},{r.Effort},{r.Isolation},{r.EscalateTo},{r.Context},{r.Prompt}"),
         .. s.Tools.Select(t => $"T:{t.Name},{t.Package},{t.Version},{string.Join(';', t.Args)}"),
         .. s.Gates.Select(g => $"G:{g.Name},{g.Kind},{g.Tool}"),
         .. s.Flow.Select(f => $"F:{f.Type},{f.Target}")]);
}
