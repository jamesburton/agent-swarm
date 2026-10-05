using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using WorktreeProgram = Swarm.Worktree.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class WorktreeCliTests
{
    const string Epic = "epic/42-auth";

    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = WorktreeProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static (TempRepo Repo, string Config) Setup(string state = EpicStates.Open)
    {
        var repo = TempRepo.Create();
        repo.Epic(name: Epic);
        new EpicStore(new StateLayout(repo.StateDir)).Save(new EpicRecord(1, "42", "auth", Epic, "main", repo.Sha("main"), DateTime.UtcNow, state, null, null, null));
        return (repo, TestConfig.Write(repo, TestConfig.For(repo)));
    }

    [Fact]
    public void Create_WithEpic_PrintsResult()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        var (code, output, _) = Run(repo, "create", "9933", "login-form", "--epic", "42", "--config", config);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("task/9933-login-form", json.GetProperty("branch").GetString());
        Assert.Equal(Epic, json.GetProperty("base").GetString());
        Assert.True(json.GetProperty("created").GetBoolean());
    }

    [Fact]
    public void Create_WithBase_Works()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        Assert.Equal(0, Run(repo, "create", "1", "x", "--base", Epic, "--config", config).Code);
    }

    [Theory]
    [InlineData("--epic", "42", "--base", "main")]
    [InlineData]
    public void Create_NeedsExactlyOneBase_Exit2(params string[] baseArgs)
    {
        var (repo, config) = Setup();
        using var _ = repo;
        var (code, output, err) = Run(repo, ["create", "1", "x", .. baseArgs, "--config", config]);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Contains("--epic", err);
    }

    [Fact]
    public void Create_UnknownOrClosedEpic_Exit3()
    {
        var (repo, config) = Setup(EpicStates.Closed);
        using var _ = repo;
        Assert.Equal(ExitCodes.BadInput, Run(repo, "create", "1", "x", "--epic", "7", "--config", config).Code);
        var (code, _, err) = Run(repo, "create", "1", "x", "--epic", "42", "--config", config);
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic '42' is closed", err);
    }

    [Fact]
    public void List_AndPruneDryRun_PrintOneJsonLine()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        Run(repo, "create", "1", "x", "--epic", "42", "--config", config);
        var list = SingleJsonLine(Run(repo, "list", "--epic", "42", "--config", config).Out);
        Assert.Equal(1, list.GetProperty("worktrees").GetArrayLength());
        var (code, output, _) = Run(repo, "prune", "--dry-run", "--config", config);
        Assert.Equal(0, code);
        var report = SingleJsonLine(output);
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.Equal("keep", report.GetProperty("items")[0].GetProperty("action").GetString());
    }

    [Fact]
    public void Prune_FailedItem_PrintsReportAndExits4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (repo, config) = Setup();
        using var _ = repo;
        var created = SingleJsonLine(Run(repo, "create", "1", "x", "--epic", "42", "--config", config).Out);
        var path = created.GetProperty("path").GetString()!;
        using var held = new FileStream(Path.Combine(path, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);
        var (code, output, err) = Run(repo, "prune", "--force", "--config", config);
        Assert.Equal(ExitCodes.Environment, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("failed").GetInt32());
        Assert.StartsWith("error: 1 prune item(s) failed (see items[].error in the report on stdout)", err.Split('\n').Last(l => l.Length > 0));
    }

    [Theory]
    [InlineData("create", "1")]
    [InlineData("nope")]
    [InlineData("prune", "--bogus")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        var (repo, _) = Setup();
        using var __ = repo;
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void OutsideRepo_Exits3()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.BadInput, WorktreeProgram.Run(["list"], new StringWriter(), new StringWriter(), dir.Dir));
    }
}
