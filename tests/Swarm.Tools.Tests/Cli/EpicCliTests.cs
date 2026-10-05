using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using EpicProgram = Swarm.Epic.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class EpicCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EpicProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static (TempRepo Repo, string Config) Opened()
    {
        var repo = TempRepo.Create();
        var config = TestConfig.Write(repo, TestConfig.For(repo));
        Assert.Equal(0, Run(repo, "open", "42", "auth", "--config", config).Code);
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Commit("9933: Login\n\nTicket: 9933\nEpic: 42-auth\nBatch: 1\nSwarm-Run: run-1", ("t1.txt", "1\n"));
        repo.Git("checkout", "-q", "main");
        return (repo, config);
    }

    static (int Code, string Out, string Err) Report(EpicCloseResult r)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EpicProgram.ReportClose(r, stdout, stderr, Verbosity.Quiet);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static EpicCloseResult Result(
        string result, IReadOnlyList<EpicBlocker>? blockers = null, IReadOnlyList<string>? files = null, IReadOnlyList<string>? warnings = null) =>
        new(SwarmJson.SchemaVersion, result, "42", "epic/42-auth", "main", null, ["9933"], blockers ?? [], [], files ?? [], "Merge epic 42-auth", false, warnings ?? []);

    static string[] Lines(string text) => text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Open_PrintsResultWithBatchEpic()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "open", "42", "auth", "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(("epic/42-auth", "42-auth"), (json.GetProperty("branch").GetString(), json.GetProperty("batchEpic").GetString()));
    }

    [Fact]
    public void Open_DisallowedKind_Exit2NoStdout()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo) with
        {
            EpicTool = new EpicSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
        };
        var (code, output, err) = Run(repo, "open", "9933", "login", "--kind", "fix", "--config", TestConfig.Write(repo, config));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Contains("silently break CI", err);
    }

    [Fact]
    public void Open_WarningsGoToStderrNotStdout()
    {
        using var repo = TempRepo.Create();

        // A {kind}/{id}-{slug} epic branch that the default epicBranchTemplate (epic/{epic}) cannot address: one warning.
        var config = TestConfig.For(repo) with
        {
            EpicTool = new EpicSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
        };
        var (code, output, err) = Run(repo, "open", "9933", "login", "--config", TestConfig.Write(repo, config));
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        var warning = Assert.Single(json.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Contains("epicBranchTemplate", warning);
        Assert.Equal("warning: " + warning, err.TrimEnd());
        Assert.DoesNotContain("warning:", output);
    }

    [Fact]
    public void ReportClose_Conflict_PrintsWarningsThenTheErrorLine()
    {
        var (code, output, err) = Report(Result(CloseResults.Conflict, files: ["shared.txt"], warnings: ["w1", "w2"]));
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal("conflict", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.Equal(
            ["warning: w1", "warning: w2", "error: merging 'epic/42-auth' into 'main' conflicts in shared.txt (merge 'main' into the epic and resolve, then close again)"],
            Lines(err));
    }

    [Fact]
    public void ReportClose_Blocked_PrintsWarningsBeforeTheErrorLine()
    {
        var blocker = new EpicBlocker(BlockerCodes.NothingToMerge, "nothing to merge", false);
        var (code, _, err) = Report(Result(CloseResults.Blocked, blockers: [blocker], warnings: ["w1"]));
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal(["warning: w1", "error: epic '42' not closed: nothing to merge (1 blocker(s); see blockers)"], Lines(err));
    }

    [Fact]
    public void ReportClose_Merged_PrintsWarningsAndNoError()
    {
        var (code, _, err) = Report(Result(CloseResults.Merged, warnings: ["w1"]));
        Assert.Equal(0, code);
        Assert.Equal(["warning: w1"], Lines(err));
    }

    [Fact]
    public void Status_AllAndOne()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var all = SingleJsonLine(Run(repo, "status", "--config", config).Out);
        var epic = Assert.Single(all.GetProperty("epics").EnumerateArray());
        Assert.True(epic.GetProperty("readyToClose").GetBoolean());
        Assert.Equal(1, SingleJsonLine(Run(repo, "status", "42", "--config", config).Out).GetProperty("epics").GetArrayLength());
        Assert.Equal(ExitCodes.BadInput, Run(repo, "status", "7", "--config", config).Code);
    }

    [Fact]
    public void Status_StrayRecordFile_IsAWarningAndAnUnreadableEntry_NotAFailure()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var stray = Path.Combine(repo.StateDir, "epics", "notes copy.json");
        File.WriteAllText(stray, "{}");

        var (code, output, err) = Run(repo, "status", "--config", config);

        Assert.Equal(ExitCodes.Ok, code);
        var json = SingleJsonLine(output);
        Assert.Equal("42", Assert.Single(json.GetProperty("epics").EnumerateArray()).GetProperty("id").GetString());
        var unreadable = Assert.Single(json.GetProperty("unreadable").EnumerateArray());
        Assert.Equal(stray, unreadable.GetProperty("path").GetString());
        var line = Assert.Single(Lines(err));
        Assert.StartsWith($"warning: epic file '{stray}' skipped: ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_Merged_Exit0()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var (code, output, err) = Run(repo, "close", "42", "--config", config);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal("merged", json.GetProperty("result").GetString());
        Assert.Equal(0, json.GetProperty("warnings").GetArrayLength());
        Assert.Empty(err);
        Assert.Equal(3, repo.Git("rev-list", "--parents", "-n", "1", "main").Split(' ').Length);
    }

    [Fact]
    public void Close_DeleteBranchCheckedOut_KeepsBranchAndPrintsWarning()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var elsewhere = Path.Combine(repo.Sandbox, "epic-co");
        repo.Git("worktree", "add", "-q", elsewhere, "epic/42-auth");
        var (code, output, err) = Run(repo, "close", "42", "--delete-branch", "--config", config);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal("merged", json.GetProperty("result").GetString());
        Assert.False(json.GetProperty("branchDeleted").GetBoolean());
        var warning = Assert.Single(json.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Equal("warning: " + warning, err.TrimEnd());
        Assert.Contains("is checked out at", warning);
        Assert.NotEmpty(repo.Git("branch", "--list", "epic/42-auth"));
    }

    [Fact]
    public void Close_DeleteBranch_DeletesMergedEpicBranch()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var (code, output, err) = Run(repo, "close", "42", "--delete-branch", "--config", config);
        Assert.Equal(0, code);
        Assert.True(SingleJsonLine(output).GetProperty("branchDeleted").GetBoolean());
        Assert.Empty(err);
        Assert.Empty(repo.Git("branch", "--list", "epic/42-auth"));
    }

    [Fact]
    public void Close_Blocked_Exit1WithJsonAndOneErrorLine()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        File.WriteAllText(Path.Combine(repo.Root, "README.md"), "edited\n");
        var (code, output, err) = Run(repo, "close", "42", "--force", "--config", config);
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal("blocked", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.StartsWith("error: epic '42' not closed: 'main' is checked out at", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void Close_Conflict_Exit1()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Commit("epic side", ("shared.txt", "epic\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main side", ("shared.txt", "main\n"));
        var (code, output, err) = Run(repo, "close", "42", "--config", config);
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal("conflict", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.Contains("conflicts in shared.txt", err);
    }

    [Fact]
    public void Close_DryRun_Exit0_NothingChanged()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var before = repo.Sha("main");
        Assert.Equal(0, Run(repo, "close", "42", "--dry-run", "--config", config).Code);
        Assert.Equal(before, repo.Sha("main"));
    }

    [Theory]
    [InlineData("open", "42")]
    [InlineData("close")]
    [InlineData("nope")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.DoesNotContain('\n', err.TrimEnd());
    }
}
