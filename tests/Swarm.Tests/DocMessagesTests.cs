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
        { Yaml("    kind: code\n    flow:", "    kind: code\n    model: haiku\n    flow:"), "unknown key 'model' in code role 'orchestrator'" },
        { Yaml("    effort: low\n", "    effort: low\n    flow: [a]\n"), "unknown key 'flow' in llm role 'worker'" },
        { Yaml("  squash:\n    package: Swarm.Squash\n    version: 0.1.0\n", "  squash: \"\"\n"), "invalid YAML (line 38): Exception during deserialization" },
        { Yaml("  squash:\n    package: Swarm.Squash\n    version: 0.1.0\n", "  squash: {}\n"), "tool 'squash': explicit package id required" },
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
        { () => Base().Llm("w", null!), "configuration delegate for role 'w' must not be null" },
        { () => Base().Llm("w", r => r.Model(null!)), "model of role 'w' must not be null" },
        { () => Base().Llm("w", r => r.Description(null!)), "description of role 'w' must not be null" },
        { () => Base().Llm("w", r => r.Tools(null!)), "tools of role 'w' must not be null" },
        { () => Base().Tool("t", null!, "1.0.0"), "package of tool 't' must not be null" },
        { () => Base().Tool("t", "P", null!), "version of tool 't' must not be null" },
        { () => Base().Tool("t", "P", "1.0.0", null!), "args of tool 't' must not be null" },
        { () => Base().Gate("g", null!), "kind of gate 'g' must not be null" },
        { () => SwarmBuilder.Define("s", "d").Orchestrator("o", null!), "flow must not be null" },
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
        { Valid("worker* tool:t", b => b.Tool("t", "P Q", "1.0.0")), "tool 't': package 'P Q' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)" },
        { Valid("worker* gate:g", b => b.Tool("t", "P", "1.0.0").Gate("g", "unit test", "t")), "gate 'g': kind 'unit test' is unsafe (allowed characters: letters, digits and _ . -)" },
        { Valid("worker* gate:g.x", b => b.Gate("g.x", "test")), $"gate 'g.x': {UnsafeName}" },
        { WithRole(r => r.Tools("Read").Description("a\u2028b")), "role 'worker': description contains a control character" },
        { WithRole(r => r.Tools("Re\u2028ad")), "role 'worker': field 'tools' has an unsafe value 'Re?ad'" },
        { WithRole(r => r.Tools("Read").Model("claude-a b")), "role 'worker': field 'model' has an unsafe value 'claude-a b'" },
        { HandBuilt(d => d with { Roles = [.. d.Roles.Select(r => r.Name == "worker" ? r with { Model = null } : r)] }), "role 'worker': model is missing" },
        { HandBuilt(d => d with { Flow = [.. d.Flow, new Stage(StageType.Gate, "ghost")] }), "flow stage 'ghost' (Gate) does not exist" },
        { HandBuilt(d => d with { Flow = [.. d.Flow, new Stage(StageType.Tool, "ghost")] }), "flow stage 'ghost' (Tool) does not exist" },
        { HandBuilt(d => d with { Flow = [new Stage(StageType.Fanout, "o")] }), "flow stage 'o' does not name an llm role" },
        { HandBuilt(d => d with { Gates = [new Gate("g", "test", "ghost")], Flow = [.. d.Flow, new Stage(StageType.Gate, "g")] }), "gate 'g': tool 'ghost' does not exist" },
        { HandBuilt(d => d with { Tools = [new ToolDef("t", "P", "1.0", [])], Flow = [.. d.Flow, new Stage(StageType.Tool, "t")] }), "tool 't': version '1.0' is not an exact pinned version" },
    };

    // A validated definition edited afterwards, as a hand-built SwarmDefinition record that skipped validation would be.
    static SwarmDefinition HandBuilt(Func<SwarmDefinition, SwarmDefinition> edit) => edit(Valid("worker*"));

    [Fact]
    public void PromptMayContainLineSeparators()
    {
        var files = AgentFileRenderer.Render(WithRole(r => r.Tools("Read").Prompt("a\u2028b")));
        Assert.Contains("a\u2028b", files[".claude/agents/worker.md"]);
    }

    [Fact]
    public void ValidateAcceptsWhatOnlyRenderRejects()
    {
        // Gate kind, tool package and tool argument with spaces pass validation; render rejects them.
        var d = Valid("worker* gate:g tool:t", b => b.Tool("t", "P Q", "1.0.0", "a b").Gate("g", "unit test", "t"));
        Assert.Empty(Validator.Check(d));
        Assert.Throws<SwarmException>(() => WorkflowRenderer.Render(d));
    }

    [Fact]
    public void BuilderPaddingBehaviour()
    {
        string Prompt(string p) => SwarmBuilder.Define("s", "d").Orchestrator("o", "w*").Llm("w", r => r.Model("haiku").Tools("Read").Prompt(p)).Build().Roles.Single(r => r.Name == "w").Prompt;
        Assert.Equal("p", Prompt("  p  "));
        var padded = SwarmBuilder.Define("s", "d").Orchestrator("o", "w*", "gate:g").Llm("w", r => r.Model("haiku").Tools("Read").Description(" d ").Prompt("p"))
            .Tool("t", " P ", "1.0.0").Gate("g", " test ", "t").Build();
        Assert.Equal(" d ", padded.Roles.Single(r => r.Name == "w").Description);
        Assert.Equal(" test ", padded.Gates.Single().Kind);
        Assert.Equal(" P ", padded.Tools.Single().Package);
        Assert.Throws<SwarmException>(() => WorkflowRenderer.Render(padded));
        Assert.Equal("role 'w': unknown model alias ' haiku ' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)",
            Assert.Throws<SwarmException>(() => SwarmBuilder.Define("s", "d").Orchestrator("o", "w*").Llm("w", r => r.Model(" haiku ").Tools("Read")).Build()).Message);
    }

    // Messages that are exact for the test but are not quoted verbatim in the document (values are test-specific).
    static readonly HashSet<string> NotInDoc =
    [
        $"role '{new string('a', 65)}': {UnsafeName}",
        $"role 'con': {UnsafeName}",
        "role 'worker': field 'tools' has an unsafe value 'Re?ad'",
        "role 'worker': field 'model' has an unsafe value 'claude-a b'",
    ];

    // Fragments of CLI and path messages (the full text contains a path or is built from options).
    static readonly string[] CliFragments =
    [
        "missing command; expected 'validate' or 'render' (see --help)",
        "a bare '--' is not supported (see --help)",
        "--out was given more than once",
        "--out must not be empty",
        "--out requires a directory",
        "--out is a file, not a directory: ",
        "unknown option '--bogus' (see --help)",
        "unexpected extra argument 'b.md'",
        "missing <file> argument for 'validate'",
        "missing required option --out <dir>",
        "unknown command 'bogus' (see --help)",
        "file not found: nope.md",
        "unsupported file extension '.txt'; expected .md, .yaml or .yml",
        "refusing to write outside the output directory: '../a'",
    ];

    [Fact]
    public void EveryPinnedMessageAppearsInTheDocument()
    {
        var root = RepoRoot();
        var doc = File.ReadAllText(Path.Combine(root, "docs", "definition-format.md")).Replace("\\|", "|");
        var pinned = MarkdownCases().Concat(YamlCases()).Concat(BuilderCases()).Concat(RenderCases())
            .Select(r => (string)r[1]).Concat(CliFragments).Where(m => !NotInDoc.Contains(m)).Distinct();
        var missing = pinned.Where(m => !doc.Contains(m, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "not in docs/definition-format.md:\n" + string.Join("\n", missing));
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "docs", "definition-format.md"))) return d.FullName;
        }

        throw new InvalidOperationException("docs/definition-format.md not found above the test output directory");
    }

    [Fact]
    public void OutputPathMessage() =>
        Assert.Equal("refusing to write outside the output directory: '../a'",
            Assert.Throws<InvalidDataException>(() => OutputPaths.Resolve(Path.GetTempPath(), "../a")).Message);

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
            Assert.Equal((2, "error: a bare '--' is not supported (see --help)"), Cli("validate", "--"));
            Assert.Equal((2, "error: --out was given more than once"), Cli("render", good, "--out", "x", "--out", "y"));
            Assert.Equal((2, "error: --out must not be empty"), Cli("render", good, "--out", ""));
            Assert.Equal((2, "error: unknown option '--bogus' (see --help)"), Cli("validate", "--bogus"));
            Assert.Equal((2, "error: unexpected extra argument 'b.md'"), Cli("validate", "a.md", "b.md"));
            Assert.Equal((2, "error: missing <file> argument for 'validate'"), Cli("validate"));
            var asFile = Path.Combine(dir, "afile");
            File.WriteAllText(asFile, "x");
            Assert.Equal((2, "error: --out is a file, not a directory: " + asFile), Cli("render", good, "--out", asFile));
            Assert.Equal(0, Cli("validate", good).Code);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
