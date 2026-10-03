using System.Text;
using Swarm.Cli;
using Swarm.Render;

public class CliTests : IDisposable
{
    const string Note = "Note: generated agent files are only visible to a Claude Code session started after they exist; restart or open a new session.";

    readonly string tmp = Path.Combine(Path.GetTempPath(), "swarm-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(tmp);

    public void Dispose()
    {
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, true);
        }
    }

    static string Sample(string file) => Path.Combine(AppContext.BaseDirectory, "Samples", file);

    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Program.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    static void AssertOneErrorLine((int Code, string Out, string Err) r, int code)
    {
        Assert.Equal(code, r.Code);
        Assert.Equal("", r.Out);
        Assert.StartsWith("error: ", r.Err);
        Assert.Single(r.Err.TrimEnd('\r', '\n').Split('\n'));
        Assert.EndsWith("\n", r.Err);
    }

    string Write(string name, string content)
    {
        var p = Path.Combine(tmp, name);
        File.WriteAllText(p, content, new UTF8Encoding(true));
        return p;
    }

    string BadModel() => Write("bad.md", TestSamples.Markdown().Replace("model: opus", "model: gpt-9", StringComparison.Ordinal));

    static KeyValuePair<string, string>[] AllRendered()
    {
        var d = TestSamples.Parsed();
        return AgentFileRenderer.Render(d).Concat(WorkflowRenderer.Render(d)).ToArray();
    }

    static string[] Relative(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    [Fact] public void ValidateMarkdownOk()
    {
        var r = Run("validate", Sample("epic-delivery.md"));
        Assert.Equal((0, "ok" + Environment.NewLine, ""), r);
    }

    [Fact] public void ValidateYamlOk() => Assert.Equal(0, Run("validate", Sample("epic-delivery.yaml")).Code);

    [Fact] public void ValidateYmlExtensionOk() => Assert.Equal(0, Run("validate", Write("x.yml", TestSamples.Yaml())).Code);

    [Fact] public void CrlfAndBomSampleValidates()
    {
        var p = Write("crlf.md", TestSamples.Markdown().Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Equal(0xEF, File.ReadAllBytes(p)[0]);
        Assert.Equal(0, Run("validate", p).Code);
    }

    [Fact] public void ValidateBadModelIsExit1WithOneLine()
    {
        var p = BadModel();
        Assert.NotEqual(TestSamples.Markdown(), File.ReadAllText(p));
        AssertOneErrorLine(Run("validate", p), 1);
    }

    [Fact] public void RenderWritesSixFilesMatchingRenderers()
    {
        var o = Path.Combine(tmp, "out");
        var r = Run("render", Sample("epic-delivery.md"), "--out", o);
        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        var expected = AllRendered();
        Assert.Equal(6, expected.Length);
        Assert.Equal(expected.Select(p => p.Key).Order(StringComparer.Ordinal), Relative(o));
        foreach (var (k, v) in expected)
        {
            Assert.Equal(v, File.ReadAllText(Path.Combine(o, k)));
        }

        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(expected.Select(p => p.Key).Concat([Note]), lines);
    }

    [Fact] public void RenderInvalidWritesNothing()
    {
        var o = Path.Combine(tmp, "out");
        AssertOneErrorLine(Run("render", BadModel(), "--out", o), 1);
        Assert.False(Directory.Exists(o) && Directory.EnumerateFileSystemEntries(o).Any());
    }

    [Fact] public void RenderTwiceIsIdempotentAndKeepsUnrelatedFiles()
    {
        var o = Path.Combine(tmp, "out");
        Assert.Equal(0, Run("render", Sample("epic-delivery.md"), "--out", o).Code);
        var extra = Path.Combine(o, "keep.txt");
        File.WriteAllText(extra, "mine");
        var first = AllRendered().ToDictionary(p => p.Key, p => File.ReadAllBytes(Path.Combine(o, p.Key)));
        Assert.Equal(0, Run("render", Sample("epic-delivery.md"), "--out", o).Code);
        foreach (var (k, v) in first)
        {
            Assert.Equal(v, File.ReadAllBytes(Path.Combine(o, k)));
        }

        Assert.Equal("mine", File.ReadAllText(extra));
    }

    [Fact] public void YamlRenderMatchesMarkdownRender()
    {
        var a = Path.Combine(tmp, "a");
        var b = Path.Combine(tmp, "b");
        Assert.Equal(0, Run("render", Sample("epic-delivery.md"), "--out", a).Code);
        Assert.Equal(0, Run("render", Sample("epic-delivery.yaml"), "--out", b).Code);
        Assert.Equal(Relative(a), Relative(b));
    }

    [Fact] public void UnknownExtensionIsExit2() => AssertOneErrorLine(Run("validate", Write("x.txt", "hi")), 2);

    [Fact] public void MissingFileIsExit2() => AssertOneErrorLine(Run("validate", Path.Combine(tmp, "nope.md")), 2);

    [Fact] public void MissingOutIsExit2() => AssertOneErrorLine(Run("render", Sample("epic-delivery.md")), 2);

    [Fact] public void OutWithoutValueIsExit2() => AssertOneErrorLine(Run("render", Sample("epic-delivery.md"), "--out"), 2);

    [Fact] public void OutIsAFileIsExit2() =>
        AssertOneErrorLine(Run("render", Sample("epic-delivery.md"), "--out", Write("afile", "x")), 2);

    [Fact] public void MissingFileArgIsExit2() => AssertOneErrorLine(Run("validate"), 2);

    [Fact] public void ExtraPositionalIsExit2() => AssertOneErrorLine(Run("validate", "a.md", "b.md"), 2);

    [Fact] public void NoArgsIsExit2() => AssertOneErrorLine(Run(), 2);

    [Fact] public void UnknownCommandIsExit2() => AssertOneErrorLine(Run("frobnicate"), 2);

    [Fact] public void UnknownOptionIsExit2()
    {
        AssertOneErrorLine(Run("validate", Sample("epic-delivery.md"), "--bogus"), 2);
        AssertOneErrorLine(Run("render", Sample("epic-delivery.md"), "--out", tmp, "--bogus"), 2);
    }

    [Fact] public void OutOnValidateIsExit2() => AssertOneErrorLine(Run("validate", Sample("epic-delivery.md"), "--out", tmp), 2);

    [Theory] [InlineData("--help")] [InlineData("-h")]
    public void HelpPrintsUsage(string flag)
    {
        var r = Run(flag);
        Assert.Equal(0, r.Code);
        Assert.Contains("swarm validate", r.Out);
        Assert.Contains("swarm render", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact] public void VersionPrintsToolVersion()
    {
        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.Matches(@"^\d+\.\d+\.\d+\S*\r?\n$", r.Out);
        Assert.DoesNotContain('+', r.Out);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/abs.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("\\\\srv\\share\\x")]
    [InlineData("")]
    public void GuardRejectsEscapes(string key) =>
        Assert.ThrowsAny<Exception>(() => OutputPaths.Resolve(tmp, key));

    [Fact] public void GuardAcceptsNestedRelativeKey() =>
        Assert.Equal(Path.Combine(Path.GetFullPath(tmp), ".claude", "agents", "x.md"), OutputPaths.Resolve(tmp, ".claude/agents/x.md"));

    [Fact] public void EscapingKeyWritesNothing()
    {
        var o = Path.Combine(tmp, "out");
        var files = new Dictionary<string, string> { ["ok.txt"] = "a", ["../evil.txt"] = "b" };
        var ex = Assert.ThrowsAny<Exception>(() => OutputPaths.WriteAll(o, files));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.False(File.Exists(Path.Combine(o, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(tmp, "evil.txt")));
    }
}
