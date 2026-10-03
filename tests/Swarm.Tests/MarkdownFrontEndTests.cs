using Swarm.Core;
using Swarm.Formats;

public class MarkdownFrontEndTests
{
    const string Flow = "flow: worker*, gate:batch-green, reviewer, tool:squash";
    const string ReviewerPrompt = "Review the diff. Report blocking issues first.";

    static string Sample => TestSamples.Markdown().ReplaceLineEndings("\n");

    [Fact] public void ParsesSample()
    {
        var s = TestSamples.Parsed();
        Assert.Equal("epic-delivery", s.Name);
        Assert.Equal(3, s.Roles.Count(r => r.Kind == RoleKind.Llm));
        Assert.Equal("opus", s.Roles.Single(r => r.Name == "expert").Model);
        Assert.Equal("expert", s.Roles.Single(r => r.Name == "worker").EscalateTo);
        Assert.Equal(new[] { StageType.Fanout, StageType.Gate, StageType.Role, StageType.Tool }, s.Flow.Select(f => f.Type));
        Assert.Equal("0.1.0", s.Tools.Single().Version);
        Assert.Equal("squash", s.Gates.Single().Tool);
    }

    [Fact] public void CrlfInput_ParsesIdentically() =>
        Assert.Equal(MarkdownFrontEnd.Parse(Sample).Flow, MarkdownFrontEnd.Parse(Sample.Replace("\n", "\r\n")).Flow);

    [Fact] public void Bom_IsTolerated() =>
        Assert.Equal("epic-delivery", MarkdownFrontEnd.Parse("﻿" + Sample).Name);

    [Fact] public void ToolsList_IsTrimmed() =>
        Assert.Equal(new[] { "Read", "Edit" },
            MarkdownFrontEnd.Parse(Sample.Replace("tools: Read, Edit, Write, Grep, Glob, Bash", "tools: Read ,  Edit")).Roles.Single(r => r.Name == "worker").Tools);

    [Fact] public void ExtraHeading_IsRejected()
    {
        var ex = Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(Sample + "\n## Notes and ideas\nfoo: bar\n"));
        Assert.Equal("unrecognised heading '## Notes and ideas' (expected '## name (code|llm)', '## tool: name' or '## gate: name')", ex.Message);
    }

    [Fact] public void BadModel_Throws() =>
        Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(Sample.Replace("model: haiku", "model: gpt5")));

    [Fact] public void MultiLinePrompt_HasNoCarriageReturns_AndMatchesAcrossLineEndings()
    {
        var lf = Sample.Replace(ReviewerPrompt, ReviewerPrompt + "\nThen summarise.");
        var a = MarkdownFrontEnd.Parse(lf).Roles.Single(r => r.Name == "reviewer").Prompt;
        var b = MarkdownFrontEnd.Parse(lf.Replace("\n", "\r\n")).Roles.Single(r => r.Name == "reviewer").Prompt;
        Assert.Contains("\n", a);
        Assert.DoesNotContain('\r', a);
        Assert.DoesNotContain('\r', b);
        Assert.Equal(a, b);
    }

    [Fact] public void PromptMayContainKeyLikeText()
    {
        var r = MarkdownFrontEnd.Parse(Sample.Replace(ReviewerPrompt, ReviewerPrompt + "\nmodel: ignored here")).Roles.Single(x => x.Name == "reviewer");
        Assert.Equal("sonnet", r.Model);
        Assert.EndsWith("model: ignored here", r.Prompt);
    }

    [Theory]
    [InlineData("escalate-to: expert", "escalate_to: expert")]
    [InlineData("model: haiku", "Model: haiku")]
    [InlineData("package: Swarm.Squash", "package: Swarm.Squash\nagrs: x")]
    [InlineData("kind: test\ntool: squash", "kind: test\ntol: squash")]
    [InlineData(Flow, "flows: worker*")]
    [InlineData("version: 0.1.0", "version: 0.1.0\nstray prose")]
    [InlineData("kind: test", "kind: test\nstray prose")]
    [InlineData("## orchestrator  (code)\n", "## orchestrator  (code)\nsome prose\n")]
    [InlineData("## expert  (llm)", "##\tNotes")]
    [InlineData("tools: Read, Edit, Write, Grep, Glob, Bash", "tools: Read,,Edit")]
    [InlineData(Flow, "flow: worker*,,reviewer")]
    public void BadSection_ThrowsOneLineError(string from, string to)
    {
        Assert.Contains(from, Sample);
        var ex = Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(Sample.Replace(from, to)));
        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact] public void StrayWhitespaceInLists_IsTrimmed()
    {
        var s = MarkdownFrontEnd.Parse(Sample.Replace("tools: Read, Edit, Write, Grep, Glob, Bash", "tools: Read ,edit").Replace("escalate-to: expert", "escalate-to:   expert  "));
        var w = s.Roles.Single(r => r.Name == "worker");
        Assert.Equal(new[] { "Read", "edit" }, w.Tools);
        Assert.Equal("expert", w.EscalateTo);
    }
}
