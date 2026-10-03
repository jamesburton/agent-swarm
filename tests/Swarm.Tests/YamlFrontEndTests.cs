using Swarm.Core;
using Swarm.Formats;

public class YamlFrontEndTests
{
    static string Sample => TestSamples.Yaml().ReplaceLineEndings("\n");

    static SwarmException Fails(string yaml)
    {
        var ex = Assert.Throws<SwarmException>(() => YamlFrontEnd.Parse(yaml));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        return ex;
    }

    [Fact] public void YamlSample_EqualsMarkdownSample()
    {
        var md = TestSamples.Parsed();
        var yml = YamlFrontEnd.Parse(TestSamples.Yaml());
        Assert.Equal(md.Name, yml.Name);
        Assert.Equal(md.Description, yml.Description);
        Assert.Equal(md.Roles.Select(r => (r.Name, r.Kind, r.Model, r.EscalateTo)), yml.Roles.Select(r => (r.Name, r.Kind, r.Model, r.EscalateTo)));
        Assert.Equal(md.Roles.Select(r => (r.Description, r.MaxTurns, r.Effort, r.Isolation, r.Context, r.Prompt)),
            yml.Roles.Select(r => (r.Description, r.MaxTurns, r.Effort, r.Isolation, r.Context, r.Prompt)));
        Assert.Equal(md.Roles.Select(r => r.Tools), yml.Roles.Select(r => r.Tools));
        Assert.Equal(md.Flow, yml.Flow);
        Assert.Equal(md.Tools.Select(t => (t.Name, t.Package, t.Version)), yml.Tools.Select(t => (t.Name, t.Package, t.Version)));
        Assert.Equal(md.Gates, yml.Gates);
    }

    [Fact] public void YamlUnknownModel_Throws() => Fails(Sample.Replace("model: haiku", "model: gpt5"));

    [Fact] public void CrlfAndBom_ParseIdentically()
    {
        var a = YamlFrontEnd.Parse(Sample);
        var b = YamlFrontEnd.Parse("\uFEFF" + Sample.Replace("\n", "\r\n"));
        Assert.Equal(a.Flow, b.Flow);
        Assert.Equal(a.Roles.Select(r => r.Prompt), b.Roles.Select(r => r.Prompt));
    }

    [Fact] public void MultiLinePrompt_HasNoCarriageReturns()
    {
        var yaml = Sample.Replace("      Review the diff. Report blocking issues first.", "      Review the diff.\n      Then summarise.").Replace("\n", "\r\n");
        var p = YamlFrontEnd.Parse(yaml).Roles.Single(r => r.Name == "reviewer").Prompt;
        Assert.Equal("Review the diff.\nThen summarise.", p);
    }

    [Theory]
    [InlineData("    escalate-to: expert", "    escalate_to: expert")]
    [InlineData("    model: haiku", "    modle: haiku")]
    [InlineData("    version: 0.1.0", "    version: 0.1.0\n    bogus: 1")]
    [InlineData("    kind: test", "    kind: test\n    extra: 1")]
    [InlineData("description: Deliver", "colour: red\ndescription: Deliver")]
    public void UnknownKeys_Throw(string from, string to) => Fails(Sample.Replace(from, to));

    [Fact] public void UnknownKey_NamesTheKey() => Assert.Contains("modle", Fails(Sample.Replace("    model: haiku", "    modle: haiku")).Message);

    [Fact] public void DuplicateKeys_Throw() => Fails(Sample.Replace("    effort: low", "    effort: low\n    effort: high"));

    [Fact] public void MalformedYaml_IsOneLineSwarmException() => Fails("name: [unclosed\nroles: {");

    [Fact] public void WrongShape_IsOneLineSwarmException() => Fails(Sample.Replace("tools: [Read, Grep, Glob]", "tools: nope"));

    [Fact] public void NonMappingDocument_Throws() => Fails("- a\n- b\n");

    [Fact] public void EmptyDocument_Throws() => Fails("");

    [Fact] public void ToolsList_IsTrimmed() =>
        Assert.Equal(new[] { "Read", "Edit" },
            YamlFrontEnd.Parse(Sample.Replace("[Read, Edit, Write, Grep, Glob, Bash]", "[\" Read \", Edit]")).Roles.Single(r => r.Name == "worker").Tools);

    [Theory]
    [InlineData("[Read, \"\"]")]
    [InlineData("[Read, \"  \"]")]
    [InlineData("[Read, ~]")]
    public void EmptyListEntries_Throw(string list) => Fails(Sample.Replace("[Read, Edit, Write, Grep, Glob, Bash]", list));

    [Fact] public void EmptyFlowEntry_Throws() => Fails(Sample.Replace("\"reviewer\", ", "\"reviewer\", \" \", "));

    [Theory]
    [InlineData("effort: low", "effort: turbo")]
    [InlineData("isolation: worktree", "isolation: docker")]
    [InlineData("maxTurns: 30", "maxTurns: 0")]
    [InlineData("maxTurns: 30", "maxTurns: many")]
    [InlineData("    kind: code", "    kind: robot")]
    public void BadValues_Throw(string from, string to) => Fails(Sample.Replace(from, to));

    [Fact] public void MissingKind_Throws() => Fails(Sample.Replace("    kind: test\n", ""));

    [Fact] public void FlowOnLlmRole_Throws() => Fails(Sample.Replace("    effort: low", "    effort: low\n    flow: [reviewer]"));

    [Fact] public void MissingName_Throws() => Fails(Sample.Replace("name: epic-delivery\n", ""));
}
