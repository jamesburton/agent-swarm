using Swarm.Core;

public class BuilderTests
{
    static SwarmBuilder Base() => SwarmBuilder.Define("s", "d").Orchestrator("o", "w");

    static SwarmDefinition Sample() => SwarmBuilder.Define("epic-delivery", "Deliver an epic with cheap workers, on-demand experts and a batched test gate.")
        .Orchestrator("orchestrator", "worker*", "gate:batch-green", "reviewer", "tool:squash")
        .Llm("worker", r => r.Model("haiku").Description("Implements exactly one task in its own worktree and returns a fixed-shape result.")
            .Tools("Read", "Edit", "Write", "Grep", "Glob", "Bash").MaxTurns(30).Effort("low").Isolation("worktree").EscalateTo("expert")
            .Prompt("You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes."))
        .Llm("expert", r => r.Model("opus").Description("Solves what a worker could not, from a distilled hand-off.")
            .Tools("Read", "Edit", "Grep", "Glob", "Bash").MaxTurns(40).Effort("high").Context("distilled")
            .Prompt("You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts."))
        .Llm("reviewer", r => r.Model("sonnet").Description("Reviews a green batch diff for correctness and style.")
            .Tools("Read", "Grep", "Glob").MaxTurns(15).Effort("medium").Prompt("Review the diff. Report blocking issues first."))
        .Tool("squash", "Swarm.Squash", "0.1.0")
        .Gate("batch-green", "test", "squash")
        .Build();

    static string Shape(SwarmDefinition s) => string.Join("|",
        [s.Name, s.Description,
         .. s.Roles.Select(r => $"R:{r.Name},{r.Kind},{r.Model},{r.Description},{string.Join(';', r.Tools)},{r.MaxTurns},{r.Effort},{r.Isolation},{r.EscalateTo},{r.Context},{r.Prompt}"),
         .. s.Tools.Select(t => $"T:{t.Name},{t.Package},{t.Version},{string.Join(';', t.Args)}"),
         .. s.Gates.Select(g => $"G:{g.Name},{g.Kind},{g.Tool}"),
         .. s.Flow.Select(f => $"F:{f.Type},{f.Target}")]);

    static string Msg(Action a) => Assert.Throws<SwarmException>(a).Message;

    [Fact] public void BuilderMatchesMarkdownSample()
    {
        var b = SwarmBuilder.Define("epic-delivery", "d")
            .Orchestrator("orchestrator", "worker*", "gate:batch-green", "reviewer", "tool:squash")
            .Llm("worker", r => r.Model("haiku").Description("d").Tools("Read", "Edit").EscalateTo("expert").Prompt("p"))
            .Llm("expert", r => r.Model("opus").Description("d").Prompt("p"))
            .Llm("reviewer", r => r.Model("sonnet").Description("d").Prompt("p"))
            .Tool("squash", "Swarm.Squash", "0.1.0")
            .Gate("batch-green", "test", "squash")
            .Build();
        Assert.Equal(new[] { StageType.Fanout, StageType.Gate, StageType.Role, StageType.Tool }, b.Flow.Select(f => f.Type));
    }

    [Fact] public void BuilderRejectsBadModel() =>
        Assert.Throws<SwarmException>(() => SwarmBuilder.Define("s", "d").Orchestrator("o").Llm("w", r => r.Model("gpt5").Prompt("p")).Build());

    [Fact] public void BuilderEqualsMarkdownAndYamlSamples()
    {
        Assert.Equal(Shape(TestSamples.Parsed()), Shape(Sample()));
        Assert.Equal(Shape(Swarm.Formats.YamlFrontEnd.Parse(TestSamples.Yaml())), Shape(Sample()));
    }

    [Fact] public void Build_Twice_YieldsEqualIndependentResults()
    {
        var b = Base().Llm("w", r => r.Model("haiku").Tools("Read").Prompt("p"));
        var (x, y) = (b.Build(), b.Build());
        Assert.Equal(Shape(x), Shape(y));
        Assert.NotSame(x.Roles.Single(r => r.Name == "w").Tools, y.Roles.Single(r => r.Name == "w").Tools);
        Assert.NotSame(x.Roles, y.Roles);
    }

    [Fact] public void UnpinnedToolVersion_IsRejected() =>
        Assert.Equal("tool 't': exact pinned version required (got '*')", Msg(() => Base().Tool("t", "P", "*").Build()));

    [Theory] [InlineData("")] [InlineData("   ")] [InlineData(null)]
    public void BlankNames_AreRejected(string? n)
    {
        Assert.Equal("name must not be empty", Msg(() => SwarmBuilder.Define(n!, "d")));
        Assert.Equal("name must not be empty", Msg(() => Base().Llm(n!, r => r.Model("haiku"))));
        Assert.Equal("name must not be empty", Msg(() => Base().Orchestrator(n!)));
        Assert.Equal("name must not be empty", Msg(() => Base().Tool(n!, "P", "1.0.0")));
        Assert.Equal("name must not be empty", Msg(() => Base().Gate(n!, "test")));
    }

    [Fact] public void NullArguments_GiveSwarmException()
    {
        Assert.Equal("configuration delegate for role 'w' must not be null", Msg(() => Base().Llm("w", null!)));
        Assert.Equal("description must not be null", Msg(() => SwarmBuilder.Define("s", null!)));
        Assert.Equal("flow must not be null", Msg(() => SwarmBuilder.Define("s", "d").Orchestrator("o", null!)));
        Assert.Equal("tools of role 'w' must not be null", Msg(() => Base().Llm("w", r => r.Tools(null!))));
        Assert.Equal("model of role 'w' must not be null", Msg(() => Base().Llm("w", r => r.Model(null!))));
        Assert.Equal("package of tool 't' must not be null", Msg(() => Base().Tool("t", null!, "1.0.0")));
        Assert.Equal("version of tool 't' must not be null", Msg(() => Base().Tool("t", "P", null!)));
        Assert.Equal("kind of gate 'g' must not be null", Msg(() => Base().Gate("g", null!)));
    }

    [Fact] public void ToolsList_IsTrimmed_AndEmptyEntriesRejected()
    {
        var s = Base().Llm("w", r => r.Model("haiku").Tools(" Read ", "Edit")).Build();
        Assert.Equal(new[] { "Read", "Edit" }, s.Roles.Single(r => r.Name == "w").Tools);
        Assert.Equal("empty tool entry in role 'w'", Msg(() => Base().Llm("w", r => r.Tools("Read", " "))));
    }

    [Fact] public void BadOptionalFields_MatchSharedMessages()
    {
        Assert.Equal("unknown effort 'extreme' in role 'w'", Msg(() => Base().Llm("w", r => r.Effort("extreme"))));
        Assert.Equal("unsupported isolation 'vm' in role 'w'", Msg(() => Base().Llm("w", r => r.Isolation("vm"))));
        Assert.Equal("bad maxTurns '0' in role 'w'", Msg(() => Base().Llm("w", r => r.MaxTurns(0))));
        Assert.Equal("bad maxTurns '-3' in role 'w'", Msg(() => Base().Llm("w", r => r.MaxTurns(-3))));
    }

    [Fact] public void DuplicateLlm_IsRejectedAtBuild() =>
        Assert.Equal("duplicate name 'w'", Msg(() => Base().Llm("w", r => r.Model("haiku")).Llm("w", r => r.Model("haiku")).Build()));

    [Fact] public void SecondOrchestrator_IsRejected() =>
        Assert.Equal("expected exactly one code orchestrator role", Msg(() => Base().Orchestrator("o2", "w").Llm("w", r => r.Model("haiku")).Build()));

    [Fact] public void MissingOrchestrator_IsRejected() =>
        Assert.Equal("expected exactly one code orchestrator role", Msg(() => SwarmBuilder.Define("s", "d").Llm("w", r => r.Model("haiku")).Build()));
}
