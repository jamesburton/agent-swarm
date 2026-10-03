using Swarm.Cli;
using Swarm.Core;
using Swarm.Formats;
using Swarm.Render;

/// <summary>Pins every error message quoted in docs/definition-format.md to the real text produced by the code.</summary>
public class DocMessagesTests
{
    const string UnsafeName = "name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)";
    const string BadModel = "unknown model alias 'gpt-9' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)";

    static string Md(string find, string replace)
    {
        var text = TestSamples.Markdown();
        Assert.Contains(find, text);
        return text.Replace(find, replace);
    }

    static string Yaml(string find, string replace)
    {
        var text = TestSamples.Yaml();
        Assert.Contains(find, text);
        return text.Replace(find, replace);
    }

    static string MarkdownError(string text) => Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(text)).Message;

    static string YamlError(string text) => Assert.Throws<SwarmException>(() => YamlFrontEnd.Parse(text)).Message;

    public static TheoryData<string, string> MarkdownCases() => new()
    {
        { Md("model: haiku\n", ""), "role 'worker': missing required model" },
        { Md("model: haiku", "model: gpt-9"), $"role 'worker': {BadModel}" },
        { Md("version: 0.1.0", "version: 0.1"), "tool 'squash': exact pinned version required (got '0.1')" },
        { Md("package: Swarm.Squash\n", ""), "tool 'squash': explicit package id required" },
        { Md("## tool: squash", "## tool: worker"), "duplicate name 'worker'" },
        { Md("## orchestrator  (code)\nflow: worker*, gate:batch-green, reviewer, tool:squash\n\n", ""), "expected exactly one code orchestrator role" },
        { Md("reviewer, tool:squash", "ghost, tool:squash"), "flow stage 'ghost' (Role) does not exist" },
        { Md("escalate-to: expert", "escalate-to: squash"), "role 'worker': escalate-to 'squash' is not an llm role" },
        { Md("kind: test\ntool: squash", "kind: test\ntool: ghost"), "gate 'batch-green': tool 'ghost' does not exist" },
        { Md("effort: low", "effort: lots"), "unknown effort 'lots' in role 'worker'" },
        { Md("isolation: worktree", "isolation: container"), "unsupported isolation 'container' in role 'worker'" },
        { Md("maxTurns: 30", "maxTurns: 0"), "bad maxTurns '0' in role 'worker'" },
        { Md("escalate-to: expert", "escalate_to: expert"), "unknown key 'escalate_to' in role 'worker'" },
        { Md("model: haiku\n", "model: haiku\nmodel: opus\n"), "duplicate key 'model' in 'worker'" },
        { Md("version: 0.1.0", "version: 0.1.0\nowner: me"), "unknown key 'owner' in tool 'squash'" },
        { Md("version: 0.1.0", "version: 0.1.0\nsome text"), "unexpected text in tool 'squash': 'some text'" },
        { Md("tools: Read, Edit, Write", "tools: Read,, Write"), "empty entry in list 'Read,, Write, Grep, Glob, Bash'" },
        { Md("reviewer, tool:squash", "re viewer, tool:squash"), "invalid flow entry 're viewer'" },
        { Md("kind: test\n", ""), "missing required key 'kind' in gate 'batch-green'" },
        { Md("## gate: batch-green", "## gate batch-green"), "unrecognised heading '## gate batch-green' (expected '## name (code|llm)', '## tool: name' or '## gate: name')" },
        { Md("name: epic-delivery\n", ""), "missing front-matter key 'name'" },
        { Md("description: Deliver an epic with cheap workers, on-demand experts and a batched test gate.\n---\n", "description: x\n"), "unterminated front-matter" },
        { Md("You implement one task.", "Note: You implement one task."), "unknown key 'Note' in role 'worker'" },
        { Md("You implement one task.", "\nNote: You implement one task."), "unknown key 'Note' in role 'worker'" },
        { Md("flow: worker*", "model: haiku\nflow: worker*"), "unknown key 'model' in code 'orchestrator'" },
    };

    [Fact]
    public void PromptStartingWithNonKeyLineIsKept()
    {
        // Workaround for the key-like first line: lead with a line that is not `Word: ...`.
        var def = MarkdownFrontEnd.Parse(Md("You implement one task.", "Notes for the worker:\nNote: You implement one task."));
        Assert.StartsWith("Notes for the worker:\nNote: You implement", def.Roles.Single(r => r.Name == "worker").Prompt);
    }

    [Theory]
    [MemberData(nameof(MarkdownCases))]
    public void MarkdownMessage(string text, string expected) => Assert.Equal(expected, MarkdownError(text));

    public static TheoryData<string, string> YamlCases() => new()
    {
        { Yaml("name: epic-delivery\n", ""), "missing key 'name'" },
        { Yaml("kind: llm", "kind: robot"), "unknown kind 'robot' in role 'worker' (expected code or llm)" },
        { Yaml("    kind: code\n", ""), "missing required key 'kind' in role 'orchestrator'" },
        { Yaml("    kind: test\n", ""), "missing required key 'kind' in gate 'batch-green'" },
        { Yaml("    effort: low\n", "    effort:\n"), "invalid YAML (line 13): key 'effort' has no value" },
        { Yaml("  squash:\n    package", "  squash: &t\n    package"), "invalid YAML (line 38): YAML anchors/aliases/merge keys are not supported; write the content explicitly" },
        { Yaml("    effort: low\n", "    effort: low\n    effort: high\n"), "invalid YAML (line 14): Duplicate key effort" },
        { Yaml("    effort: low\n", "    effort: low\n    colour: red\n"), "invalid YAML (line 14): Property 'colour' not found on type 'Swarm.Formats.YamlFrontEnd+RoleDto'." },
        { "", "empty or non-mapping YAML document" },
        { "a: [", "invalid YAML (line 2): While parsing a node, did not find expected node content." },
        { Yaml("    effort: low\n", "    effort: lots\n"), "unknown effort 'lots' in role 'worker'" },
        { Yaml("    version: 0.1.0\n", "    version: 1.0\n"), "tool 'squash': exact pinned version required (got '1.0')" },
        { Yaml("\"gate:batch-green\", ", "\"\", "), "empty entry in role 'orchestrator' flow" },
        { Yaml("tools: [Read, Grep, Glob]", "tools: [Read, \"\", Glob]"), "empty entry in role 'reviewer' tools" },
    };

    [Theory]
    [MemberData(nameof(YamlCases))]
    public void YamlMessage(string text, string expected) => Assert.Equal(expected, YamlError(text));

    static SwarmBuilder Base() => SwarmBuilder.Define("s", "d").Orchestrator("o", "w");

    public static TheoryData<Action, string> BuilderCases() => new()
    {
        { () => SwarmBuilder.Define(" ", "d"), "name must not be empty" },
        { () => SwarmBuilder.Define("s", null!), "description must not be null" },
        { () => SwarmBuilder.Define("s", "d").Orchestrator("o", ""), "empty flow entry" },
        { () => Base().Llm("w", r => r.Model(" haiku ")).Build(), "role 'w': unknown model alias ' haiku ' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)" },
        { () => Base().Llm("w", r => r.Tools("Read", " ")), "empty tool entry in role 'w'" },
    };

    [Theory]
    [MemberData(nameof(BuilderCases))]
    public void BuilderMessage(Action act, string expected) => Assert.Equal(expected, Assert.Throws<SwarmException>(act).Message);

    static string RenderError(SwarmDefinition d) => Assert.Throws<SwarmException>(() =>
        AgentFileRenderer.Render(d).Concat(WorkflowRenderer.Render(d)).ToList()).Message;

    static SwarmDefinition Valid(string flow, Action<SwarmBuilder>? more = null)
    {
        var b = SwarmBuilder.Define("s", "d").Orchestrator("o", flow.Split(' '))
            .Llm("worker", r => r.Model("haiku").Tools("Read").Prompt("p"))
            .Llm("reviewer", r => r.Model("haiku").Tools("Read").Prompt("p"));
        more?.Invoke(b);
        return b.Build();
    }

    static SwarmDefinition Extra(string name) => Valid("worker*", b => b.Llm(name, r => r.Model("haiku").Tools("Read").Prompt("p")));

    static SwarmDefinition WithRole(Func<RoleBuilder, RoleBuilder> cfg, string name = "worker") =>
        SwarmBuilder.Define("s", "d").Orchestrator("o", name).Llm(name, r => cfg(r.Model("haiku").Prompt("p"))).Build();

    public static TheoryData<SwarmDefinition, string> RenderCases() => new()
    {
        { WithRole(r => r), "role 'worker': tools list is empty (Claude Code would grant all tools); list tools explicitly" },
        { WithRole(r => r.Tools("Bash(git commit:*)")), "role 'worker': field 'tools' has an unsafe value 'Bash(git commit:*)'" },
        { WithRole(r => r.Tools("Read,Edit")), "role 'worker': field 'tools' has an unsafe value 'Read,Edit'" },
        { Extra("bad name"), $"role 'bad name': {UnsafeName}" },
        { Extra(new string('a', 65)), $"role '{new string('a', 65)}': {UnsafeName}" },
        { Extra("con"), $"role 'con': {UnsafeName}" },
        { Extra("Worker"), "role 'Worker': name collides case-insensitively with another role" },
        { WithRole(r => r.Tools("Read").Description("a\u0001b")), "role 'worker': description contains a control character" },
        { WithRole(r => r.Tools("Read").Prompt("a\u0001b")), "role 'worker': prompt contains a control character" },
        { Valid("worker*") with { Name = "bad name" }, $"swarm 'bad name': {UnsafeName}" },
        { Valid("reviewer worker*"), "role 'reviewer': a Role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work)" },
        { Valid("worker* tool:t", b => b.Tool("t", "P", "1.0.0", "--yes")), "tool 't': argument '--yes' is not allowed (dnx must not auto-confirm)" },
        { Valid("worker* tool:t", b => b.Tool("t", "P", "1.0.0", "a b")), "tool 't': argument 'a b' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)" },
    };

    [Theory]
    [MemberData(nameof(RenderCases))]
    public void RenderMessage(SwarmDefinition d, string expected) => Assert.Equal(expected, RenderError(d));

    static (int Code, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Program.Run(args, o, e);
        return (code, e.ToString().TrimEnd());
    }

    [Fact]
    public void CliExitCodes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "swarm-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(AppContext.BaseDirectory, "Samples", "epic-delivery.md");
            var bad = Path.Combine(dir, "bad.md");
            File.WriteAllText(bad, Md("model: haiku", "model: gpt-9"));
            var invalid = (1, $"error: role 'worker': {BadModel}");
            Assert.Equal(invalid, Cli("validate", bad));
            Assert.Equal(invalid, Cli("render", bad, "--out", Path.Combine(dir, "o")));
            Assert.False(Directory.Exists(Path.Combine(dir, "o")));
            Assert.Equal((2, "error: file not found: nope.md"), Cli("validate", "nope.md"));
            Assert.Equal((2, "error: unsupported file extension '.txt'; expected .md, .yaml or .yml"), Cli("validate", "a.txt"));
            Assert.Equal((2, "error: missing required option --out <dir>"), Cli("render", good));
            Assert.Equal((2, "error: --out requires a directory"), Cli("render", good, "--out", "-foo"));
            Assert.Equal((2, "error: unknown command 'bogus' (see --help)"), Cli("bogus"));
            Assert.Equal((2, "error: missing command; expected 'validate' or 'render' (see --help)"), Cli());
            Assert.Equal(0, Cli("validate", good).Code);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
