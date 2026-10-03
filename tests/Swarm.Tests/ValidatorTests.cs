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
        Assert.Throws<SwarmException>(() => Validator.Validated(Make([Llm("worker")])));
}
