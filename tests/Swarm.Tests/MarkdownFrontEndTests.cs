using Swarm.Core;
using Swarm.Formats;

public class MarkdownFrontEndTests
{
    static string Sample => TestSamples.Markdown();

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
        Assert.Equal("epic-delivery", MarkdownFrontEnd.Parse("\uFEFF" + Sample).Name);

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
}
