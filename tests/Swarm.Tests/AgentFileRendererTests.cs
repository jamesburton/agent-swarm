using Swarm.Core;
using Swarm.Render;

public class AgentFileRendererTests
{
    static SwarmDefinition Sample() => TestSamples.Parsed();

    static SwarmBuilder Base() => SwarmBuilder.Define("s", "d").Orchestrator("o", "worker");

    static SwarmDefinition WithWorker(string name = "worker", string description = "d", string prompt = "p") =>
        SwarmBuilder.Define("s", "d").Orchestrator("o", name).Llm(name, r => r.Model("haiku").Description(description).Tools("Read").Prompt(prompt)).Build();

    static string Worker(string description = "d", string prompt = "p") =>
        AgentFileRenderer.Render(WithWorker("worker", description, prompt))[".claude/agents/worker.md"];

    static string Msg(Action a)
    {
        var m = Assert.Throws<SwarmException>(a).Message;
        Assert.DoesNotContain('\n', m);
        return m;
    }

    [Fact] public void RendersOneFilePerLlmRole()
    {
        var files = AgentFileRenderer.Render(Sample());
        Assert.Equal(new[] { ".claude/agents/expert.md", ".claude/agents/reviewer.md", ".claude/agents/worker.md" }, files.Keys.OrderBy(k => k));
    }

    [Fact] public void FrontMatterIsValid_AndOnlyDocumentedKeys()
    {
        var allowed = new HashSet<string> { "name", "description", "model", "tools", "disallowedTools", "maxTurns", "effort", "isolation", "permissionMode", "skills", "background", "color" };
        foreach (var (_, text) in AgentFileRenderer.Render(Sample()))
        {
            var lines = text.Split('\n');
            Assert.Equal("---", lines[0]);
            var end = Array.IndexOf(lines, "---", 1);
            Assert.True(end > 0);
            foreach (var l in lines[1..end]) Assert.Contains(l.Split(':')[0], allowed);
        }
    }

    [Fact] public void OutputIsLfOnly_WithTrailingNewline() => Assert.All(AgentFileRenderer.Render(Sample()).Values, t =>
    {
        Assert.DoesNotContain('\r', t);
        Assert.EndsWith("\n", t);
        Assert.DoesNotContain("\n\n\n", t);
    });

    [Fact] public void DistilledContextAddsNoPriorConversationNote() =>
        Assert.Contains("NO prior conversation", AgentFileRenderer.Render(Sample())[".claude/agents/expert.md"]);

    [Fact] public void NonDistilledRoleHasNoNote() =>
        Assert.DoesNotContain("NO prior conversation", AgentFileRenderer.Render(Sample())[".claude/agents/worker.md"]);

    [Fact] public void RendersExpectedWorkerFile()
    {
        var text = AgentFileRenderer.Render(WithWorker())[".claude/agents/worker.md"];
        Assert.Equal("---\nname: worker\ndescription: \"d\"\nmodel: haiku\ntools: Read\n---\np\n", text);
    }

    [Fact] public void OptionalKeysAreEmittedAndToolsAreCommaJoined()
    {
        var s = Base().Llm("worker", r => r.Model("haiku").Description("d").Tools("Read", "Edit").MaxTurns(7).Effort("low").Isolation("worktree").Prompt("p")).Build();
        Assert.Equal("---\nname: worker\ndescription: \"d\"\nmodel: haiku\ntools: Read, Edit\nmaxTurns: 7\neffort: low\nisolation: worktree\n---\np\n",
            AgentFileRenderer.Render(s)[".claude/agents/worker.md"]);
    }

    [Fact] public void DescriptionWithQuotesIsEscaped() =>
        Assert.Contains("\ndescription: \"He said \\\"go\\\"\"\n", Worker("He said \"go\""));

    [Fact] public void DescriptionBackslashIsEscaped() =>
        Assert.Contains("\ndescription: \"a\\\\b\"\n", Worker("a\\b"));

    [Fact] public void DescriptionNewlineIsEscapedAsBackslashN()
    {
        var text = Worker("one\ntwo");
        Assert.Contains("\ndescription: \"one\\ntwo\"\n", text);
        Assert.Equal("---", text.Split('\n')[0]);
    }

    [Fact] public void DescriptionColonAndHashStayInsideQuotes() =>
        Assert.Contains("\ndescription: \"Fix: a # b\"\n", Worker("Fix: a # b"));

    [Fact] public void DescriptionOtherControlCharacterIsRejected() =>
        Assert.Contains("control character", Msg(() => Worker("a\u0001b")));

    [Fact] public void PromptWithDashLineDoesNotCorruptFrontMatter()
    {
        var text = Worker(prompt: "before\n---\nafter");
        var lines = text.Split('\n');
        Assert.Equal("---", lines[0]);
        Assert.Equal(5, Array.IndexOf(lines, "---", 1));
        Assert.Equal("before", lines[6]);
        Assert.Equal("---", lines[7]);
        Assert.Equal("after", lines[8]);
    }

    [Fact] public void PromptCrLfIsNormalisedToLf() =>
        Assert.DoesNotContain('\r', Worker(prompt: "a\r\nb\rc"));

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\b")]
    [InlineData("a.b")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("Com1")]
    [InlineData("lpt9")]
    public void UnsafeRoleNamesAreRejected(string name) =>
        Assert.Contains("not a safe file name", Msg(() => AgentFileRenderer.Render(WithWorker(name))));

    [Fact] public void RoleNamesDifferingOnlyByCaseAreRejected()
    {
        var s = Base().Llm("Worker", r => r.Model("haiku").Description("d").Tools("Read").Prompt("p"))
            .Llm("worker", r => r.Model("haiku").Description("d").Tools("Read").Prompt("p")).Build();
        Assert.Contains("case-insensitively", Msg(() => AgentFileRenderer.Render(s)));
    }

    [Fact] public void OutputIsDeterministicAndInDeclarationOrder()
    {
        var a = AgentFileRenderer.Render(Sample());
        var b = AgentFileRenderer.Render(Sample());
        Assert.Equal(a.Select(p => p.Key), b.Select(p => p.Key));
        Assert.Equal(a.Select(p => p.Value), b.Select(p => p.Value));
        Assert.Equal(Sample().Roles.Where(r => r.Kind == RoleKind.Llm).Select(r => $".claude/agents/{r.Name}.md"), a.Keys);
    }
}
