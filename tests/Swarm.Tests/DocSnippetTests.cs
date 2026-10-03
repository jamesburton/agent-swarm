using System.Runtime.CompilerServices;
using Swarm.Core;
using Swarm.Render;
using static TestSamples;

/// <summary>Keeps the C# builder snippet in docs/definition-format.md compiling, equal to the sample and identical to the doc text.</summary>
public class DocSnippetTests
{
    const string Begin = "// snippet begin";
    const string End = "// snippet end";

    [Fact]
    public void DocSnippetBuildsSampleEquivalent()
    {
        // snippet begin
        var swarm = SwarmBuilder.Define("epic-delivery", "Deliver an epic with cheap workers, on-demand experts and a batched test gate.")
            .Orchestrator("orchestrator", "worker*", "gate:batch-green", "reviewer", "tool:squash")
            .Llm("worker", r => r
                .Model("haiku")
                .Description("Implements exactly one task in its own worktree and returns a fixed-shape result.")
                .Tools("Read", "Edit", "Write", "Grep", "Glob", "Bash")
                .MaxTurns(30).Effort("low").Isolation("worktree").EscalateTo("expert")
                .Prompt("You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes."))
            .Llm("expert", r => r
                .Model("opus")
                .Description("Solves what a worker could not, from a distilled hand-off.")
                .Tools("Read", "Edit", "Grep", "Glob", "Bash")
                .MaxTurns(40).Effort("high").Isolation("worktree").Context("distilled")
                .Prompt("You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts."))
            .Llm("reviewer", r => r
                .Model("sonnet")
                .Description("Reviews a green batch diff for correctness and style.")
                .Tools("Read", "Grep", "Glob", "Bash")
                .MaxTurns(15).Effort("medium")
                .Prompt("Review the diff. Report blocking issues first."))
            // NOT REAL: Swarm.Squash and Swarm.TestGate are example package ids; review every package id before running the runbook.
            .Tool("squash", "Swarm.Squash", "0.1.0")
            .Tool("testgate", "Swarm.TestGate", "0.1.0")
            .Gate("batch-green", "test", "testgate")
            .Build();

        // Relative path (under the output directory) to file content, exactly as `swarm render` writes them.
        var files = SwarmRenderer.Render(swarm);
        // snippet end

        Assert.Equal(Shape(TestSamples.Parsed()), Shape(swarm));
        Assert.Equal(6, files.Count);
        var expected = AgentFileRenderer.Render(TestSamples.Parsed()).Concat(WorkflowRenderer.Render(TestSamples.Parsed()));
        Assert.Equal(expected, files);
    }

    [Fact]
    public void DocSnippetTextMatchesDocument()
    {
        var doc = FindUp("docs/definition-format.md");
        Assert.NotNull(doc);
        var docLines = File.ReadAllLines(doc!);
        var start = Array.FindIndex(docLines, l => l.Trim() == "```csharp");
        Assert.True(start >= 0, "no csharp block in the document");
        var end = Array.FindIndex(docLines, start + 1, l => l.Trim() == "```");
        var fromDoc = docLines[(start + 1)..end].Where(l => !l.StartsWith("using ")).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var src = File.ReadAllLines(SourcePath());
        var b = Array.FindIndex(src, l => l.Trim() == Begin);
        var e = Array.FindIndex(src, b + 1, l => l.Trim() == End);
        var fromTest = src[(b + 1)..e].Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.Equal(fromTest, fromDoc);
    }

    // The "real output" blocks in the document must be exactly what the renderer produces for the sample (no hand edits, no drift).
    [Fact]
    public void DocShowsTheSampleAndEveryGeneratedFileVerbatim()
    {
        var doc = File.ReadAllText(FindUp("docs/definition-format.md")!).ReplaceLineEndings("\n");
        static string Block(string text) => "\n" + text.ReplaceLineEndings("\n").TrimEnd('\n') + "\n```\n";
        Assert.True(doc.Contains(Block(TestSamples.Markdown()), StringComparison.Ordinal), "the Markdown sample is not shown verbatim");
        foreach (var (key, text) in SwarmRenderer.Render(TestSamples.Parsed()))
            Assert.True(doc.Contains(Block(text), StringComparison.Ordinal), $"{key} is not shown verbatim");
        var printed = string.Join("\n", SwarmRenderer.Render(TestSamples.Parsed()).Select(f => "wrote " + f.Key));
        Assert.Contains("```text\n" + printed + "\nNote: generated agent files", doc);
    }

    static string SourcePath([CallerFilePath] string path = "") => path;

    static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
