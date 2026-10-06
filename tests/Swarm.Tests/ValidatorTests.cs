using Swarm.Core;
using Xunit;

public class ValidatorTests
{
    static Role Llm(string n, string? model = "haiku", string? esc = null) =>
        new(n, RoleKind.Llm, model, "d", ["Read"], null, null, null, esc, null, "prompt");
    static Role Code(string n = "orch") =>
        new(n, RoleKind.Code, null, "d", [], null, null, null, null, null, "");
    static SwarmDefinition Make(IEnumerable<Role>? roles = null, IEnumerable<ToolDef>? tools = null,
                      IEnumerable<Gate>? gates = null, IEnumerable<Stage>? flow = null) =>
        new("s", "d", (roles ?? [Code(), Llm("worker")]).ToList(), (tools ?? []).ToList(),
            (gates ?? []).ToList(), (flow ?? [new Stage(StageType.Fanout, "worker")]).ToList());

    [Fact] public void GateWithUnknownTool_IsRejected() =>
        Assert.Contains(Validator.Check(Make(gates: [new Gate("g", "test", "nope")])), e => e == "gate 'g': tool 'nope' does not exist");

    [Fact] public void GateWithExistingToolOrNoTool_IsAccepted() =>
        Assert.Empty(Validator.Check(Make(tools: [new ToolDef("squash", "Pkg.Id", "1.0.0", [])], gates: [new Gate("g", "test", "squash"), new Gate("h", "test", null)])));

    [Theory] [InlineData("1.2.3", true)] [InlineData("1.2.3-rc.1", true)] [InlineData("1.2", false)] [InlineData("*", false)] [InlineData("", false)] [InlineData(null, false)]
    public void IsPinnedVersion_MatchesExactVersionsOnly(string? v, bool expected) => Assert.Equal(expected, Validator.IsPinnedVersion(v));

    [Fact] public void ValidSwarm_HasNoErrors() => Assert.Empty(Validator.Check(Make()));

    [Fact] public void UnknownModel_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "gpt5")])), e => e.Contains("unknown model alias 'gpt5'"));

    [Fact] public void MissingModel_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", null)])), e => e.Contains("missing required model"));

    [Fact] public void ClaudeIdModel_IsAccepted() =>
        Assert.Empty(Validator.Check(Make([Code(), Llm("worker", "claude-haiku-4-5-20251001")])));

    [Fact] public void DuplicateAcrossKinds_IsRejected() =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("worker", "Pkg.Id", "1.0.0", [])])),
            e => e.Contains("duplicate name 'worker'"));

    [Fact] public void MissingOrchestrator_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Llm("worker")])), e => e.Contains("exactly one code orchestrator"));

    [Fact] public void FlowToMissingTarget_IsRejected() =>
        Assert.Contains(Validator.Check(Make(flow: [new Stage(StageType.Gate, "nope")])), e => e.Contains("flow stage 'nope'"));

    [Theory] [InlineData("*")] [InlineData("1.*")] [InlineData("[1.0,2.0)")] [InlineData("")] [InlineData("latest")]
    public void UnpinnedToolVersion_IsRejected(string v) =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("squash", "Pkg.Id", v, [])])), e => e.Contains("exact pinned version"));

    [Fact] public void ToolWithoutPackage_IsRejected() =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("squash", "", "1.0.0", [])])), e => e.Contains("explicit package id"));

    [Fact] public void EscalateToUnknownOrNonLlm_IsRejected()
    {
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "haiku", "ghost")])), e => e.Contains("escalate-to 'ghost'"));
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "haiku", "orch")])), e => e.Contains("escalate-to 'orch'"));
    }

    [Fact] public void Validated_ThrowsFirstError() =>
        Assert.Equal("expected exactly one code orchestrator role", Assert.Throws<SwarmException>(() => Validator.Validated(Make([Llm("worker")]))).Message);

    static string First(SwarmDefinition s) => Assert.Throws<SwarmException>(() => Validator.Validated(s)).Message;

    [Fact] public void PinnedVersionRejectsTrailingNewline() => Assert.False(Validator.IsPinnedVersion("1.2.3\n"));

    [Theory] [InlineData("")] [InlineData("  ")]
    public void EmptyNames_AreRejected(string n)
    {
        Assert.Equal("swarm name must not be empty", First(Make() with { Name = n }));
        Assert.Equal("role name must not be empty", First(Make([Code(), Llm("worker"), Llm(n)])));
        Assert.Equal("tool name must not be empty", First(Make(tools: [new ToolDef(n, "Pkg", "1.0.0", [])])));
        Assert.Equal("gate name must not be empty", First(Make(gates: [new Gate(n, "test", null)])));
    }

    [Fact] public void NamesDifferingOnlyByCase_AreRejected()
    {
        Assert.Equal("names 'worker' and 'Worker' differ only by case", First(Make([Code(), Llm("worker"), Llm("Worker")])));
        Assert.Equal("names 'worker' and 'WORKER' differ only by case", First(Make(tools: [new ToolDef("WORKER", "Pkg", "1.0.0", [])])));
        Assert.Equal("duplicate name 'worker'", First(Make([Code(), Llm("worker"), Llm("worker")])));
    }

    [Theory]
    [InlineData("yes")] [InlineData("No")] [InlineData("ON")] [InlineData("off")] [InlineData("y")] [InlineData("N")]
    [InlineData("null")] [InlineData("True")] [InlineData("false")] [InlineData("~")]
    [InlineData("123")] [InlineData("1e3")] [InlineData("0x1F")] [InlineData("0o17")] [InlineData("-1")] [InlineData("1_000")]
    [InlineData("2026-10-03")] [InlineData("2026-1-3")]
    public void RoleNamesReadAsYamlValues_AreRejected(string n) =>
        Assert.Equal($"role '{n}': name reads as a YAML value (null, true, false, yes, no, on, off, y, n, ~, a number or a date); choose another name",
            First(Make([Code(), Llm(n)], flow: [new Stage(StageType.Fanout, n)])));

    [Theory] [InlineData("yesman")] [InlineData("n1")] [InlineData("e1")] [InlineData("1a")] [InlineData("on-call")] [InlineData("2026-10")] [InlineData("a2026-10-03")]
    public void RoleNamesThatOnlyResembleYamlValues_AreAccepted(string n) =>
        Assert.Empty(Validator.Check(Make([Code(), Llm(n)], flow: [new Stage(StageType.Fanout, n)])));

    [Theory] [InlineData("-")] [InlineData("---")] [InlineData("_")]
    public void RoleNamesWithoutALetterOrDigit_AreRejected(string n) =>
        Assert.Equal($"role '{n}': name must contain a letter or a digit",
            First(Make([Code(), Llm(n)], flow: [new Stage(StageType.Fanout, n)])));

    [Fact] public void EmptyFlow_IsRejected() =>
        Assert.Equal("flow is empty (the orchestrator needs at least one stage)", First(Make(flow: [])));

    [Fact] public void UnusedLlmRole_IsRejected() =>
        Assert.Equal("role 'idle' is not used by the flow or any escalate-to", First(Make([Code(), Llm("worker"), Llm("idle")])));

    [Fact] public void RoleUsedOnlyByEscalateTo_IsAccepted() =>
        Assert.Empty(Validator.Check(Make([Code(), Llm("worker", esc: "expert"), Llm("expert")])));

    [Theory] [InlineData("P Q")] [InlineData("-x")] [InlineData(".x")] [InlineData("_x")] [InlineData("a/b")] [InlineData("a;b")] [InlineData("a@1")] [InlineData("a\nb")]
    public void PackageThatIsNotANuGetId_IsRejected(string p) =>
        Assert.Equal($"tool 'squash': package '{p.Replace('\n', '?')}' is not a NuGet package id (letters, digits, '_', '.' or '-', starting with a letter or digit)",
            First(Make(tools: [new ToolDef("squash", p, "1.0.0", [])])));

    [Theory] [InlineData("Pkg.Id")] [InlineData("a")] [InlineData("AgentSwarm.TestGate")] [InlineData("1abc")] [InlineData("a_b-c")]
    public void NuGetStylePackageIds_AreAccepted(string p) =>
        Assert.Empty(Validator.Check(Make(tools: [new ToolDef("squash", p, "1.0.0", [])])));
}
