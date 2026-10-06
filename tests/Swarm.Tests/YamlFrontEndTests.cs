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

    [Theory]
    [InlineData("    model: haiku", "    modle: haiku", "unknown key 'modle' in role 'worker'")]
    [InlineData("    kind: code\n", "    kind: code\n    colour: red\n", "unknown key 'colour' in role 'orchestrator'")]
    [InlineData("    package: AgentSwarm.TestGate", "    package: AgentSwarm.TestGate\n    owner: me", "unknown key 'owner' in tool 'testgate'")]
    [InlineData("    tool: testgate", "    tool: testgate\n    when: always", "unknown key 'when' in gate 'batch-green'")]
    [InlineData("roles:\n", "colour: red\nroles:\n", "unknown top-level key 'colour'")]
    public void UnknownKeys_NameKeyAndOwner_NotInternalTypes(string from, string to, string message)
    {
        Assert.Contains(from, Sample);
        var m = Fails(Sample.Replace(from, to)).Message;
        Assert.Equal(message, m);
        Assert.DoesNotContain("Dto", m);
    }

    [Fact] public void DuplicateKeys_Throw() => Fails(Sample.Replace("    effort: low", "    effort: low\n    effort: high"));

    [Fact] public void MalformedYaml_IsOneLineSwarmException() => Fails("name: [unclosed\nroles: {");

    [Fact] public void WrongShape_IsOneLineSwarmException() => Assert.Contains("invalid YAML", Fails(Sample.Replace("tools: [Read, Grep, Glob, Bash]", "tools: nope")).Message);

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
    [InlineData("effort: low", "effort: turbo", "unknown effort 'turbo'")]
    [InlineData("isolation: worktree", "isolation: docker", "unsupported isolation 'docker'")]
    [InlineData("maxTurns: 30", "maxTurns: 0", "bad maxTurns '0'")]
    [InlineData("maxTurns: 30", "maxTurns: many", "bad maxTurns 'many'")]
    [InlineData("    kind: llm\n    model: haiku", "    kind: robot\n    model: haiku", "unknown kind 'robot'")]
    public void BadValues_Throw(string from, string to, string expected) => Assert.Contains(expected, Fails(Sample.Replace(from, to)).Message);

    [Fact] public void MissingKind_Throws() =>
        Assert.Contains("missing required key 'kind' in role 'worker'", Fails(Sample.Replace("    kind: llm\n    model: haiku", "    model: haiku")).Message);

    [Fact] public void MissingGateKind_Throws() =>
        Assert.Contains("missing required key 'kind' in gate 'batch-green'", Fails(Sample.Replace("    kind: test\n", "")).Message);

    [Fact] public void ScalarTyping_IsPinned()
    {
        Assert.Contains("pinned version", Fails(Sample.Replace("version: 0.1.0", "version: 1.0")).Message);
        Assert.Equal("1.2.3", YamlFrontEnd.Parse(Sample.Replace("version: 0.1.0", "version: 1.2.3")).Tools.First().Version);
        Assert.Equal(30, YamlFrontEnd.Parse(Sample).Roles.Single(r => r.Name == "worker").MaxTurns);
        Assert.Equal(30, YamlFrontEnd.Parse(Sample.Replace("maxTurns: 30", "maxTurns: \"30\"")).Roles.Single(r => r.Name == "worker").MaxTurns);
        Assert.Contains("bad maxTurns 'abc'", Fails(Sample.Replace("maxTurns: 30", "maxTurns: abc")).Message);
        Assert.Contains("unknown model alias '5'", Fails(Sample.Replace("model: haiku", "model: 5")).Message);
        Assert.Equal("true", YamlFrontEnd.Parse(Sample.Replace("name: epic-delivery", "name: true")).Name);
    }

    [Theory]
    [InlineData("name: epic-delivery", "name: &x epic-delivery")]
    [InlineData("name: epic-delivery", "name: &x epic-delivery\nx: *x")]
    [InlineData("  worker:\n", "  <<: {kind: llm}\n  worker:\n")]
    public void AnchorsAliasesAndMergeKeys_AreRejected(string from, string to) =>
        Assert.Contains("anchors/aliases/merge keys are not supported", Fails(Sample.Replace(from, to)).Message);

    [Theory]
    [InlineData("[Read, Edit, Write, Grep, Glob, Bash]", "[&a Read, Edit]")]
    [InlineData("[Read, Edit, Write, Grep, Glob, Bash]", "[&a Read, *a]")]
    [InlineData("[Read, Edit, Write, Grep, Glob, Bash]", "[Read, [&a Edit]]")]
    [InlineData("[Read, Edit, Write, Grep, Glob, Bash]", "[Read, &a [Edit]]")]
    [InlineData("[Read, Edit, Write, Grep, Glob, Bash]", "[Read, &a {x: y}]")]
    [InlineData("[@Q@worker*@Q@,", "[&a @Q@worker*@Q@, *a,")]
    public void AnchorsInsideSequences_AreRejected(string from, string to) =>
        Assert.Contains("anchors/aliases/merge keys are not supported", Fails(Sample.Replace(from.Replace("@Q@", "\""), to.Replace("@Q@", "\""))).Message);

    [Fact] public void NullToolBody_Throws() => Assert.Contains("key 'squash' has no value", Fails(Sample.Replace("  squash:\n    package: AgentSwarm.Squash\n    version: 0.1.0\n", "  squash:\n")).Message);

    [Fact] public void NullGateBody_Throws() => Assert.Contains("key 'batch-green' has no value", Fails(Sample.Replace("  batch-green:\n    kind: test\n    tool: testgate\n", "  batch-green:\n")).Message);

    [Fact] public void ScalarWhereMappingExpected_Throws() => Assert.Contains("invalid YAML", Fails(Sample.Replace("gates:\n  batch-green:\n    kind: test\n    tool: testgate\n", "gates: nope\n")).Message);

    [Fact] public void EmptyEscalateTo_Throws() =>
        Assert.Contains("key 'escalate-to' has no value", Fails(Sample.Replace("escalate-to: expert", "escalate-to:")).Message);

    [Fact] public void FlowOnLlmRole_Throws() => Fails(Sample.Replace("    effort: low", "    effort: low\n    flow: [reviewer]"));

    [Fact] public void MissingName_Throws() => Fails(Sample.Replace("name: epic-delivery\n", ""));
}
