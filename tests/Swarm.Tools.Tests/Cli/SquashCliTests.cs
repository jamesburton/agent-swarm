using System.Text.Json;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using static Swarm.Tools.Tests.Support.SquashFixture;
using SquashProgram = Swarm.Squash.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class SquashCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = SquashProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static string Config(TempRepo repo, Func<SwarmConfig, SwarmConfig>? tweak = null) =>
        TestConfig.Write(repo, tweak is null ? TestConfig.For(repo) : tweak(TestConfig.For(repo)));

    static TempRepo RepoWithParserBranch()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        repo.Git("branch", "task/9933-parser", "epic/E1");
        CommitAs(repo, "task/9933-parser", "Ada", "ada@example.invalid", "add parser", ("p.txt", "p\n"));
        CommitAs(repo, "task/9933-parser", "Ada", "ada@example.invalid", "fix parser", ("p.txt", "p2\n"));
        return repo;
    }

    [Fact]
    public void Run_LandsOneTrailerStampedCommit_Exit0()
    {
        using var repo = RepoWithParserBranch();
        var before = repo.Sha("epic/E1");
        var branchTip = repo.Sha("task/9933-parser");
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo));
        Assert.Equal(ExitCodes.Ok, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("9933", json.GetProperty("ticket").GetString());
        Assert.False(json.GetProperty("empty").GetBoolean());
        Assert.Equal(repo.Sha("epic/E1"), json.GetProperty("commit").GetString());
        Assert.Equal(before, repo.Sha("epic/E1~1"));
        Assert.Equal("9933: add parser", repo.Git("log", "-1", "--format=%s", "epic/E1"));
        Assert.Equal("Ada", repo.Git("log", "-1", "--format=%an", "epic/E1"));
        Assert.Equal("0", Trailers(repo, "epic/E1", "Batch"));
        Assert.StartsWith("squash-", Trailers(repo, "epic/E1", "Swarm-Run"));
        Assert.Equal(branchTip, repo.Sha("task/9933-parser"));
    }

    [Fact]
    public void Run_Twice_SecondIsEmpty_Exit0()
    {
        using var repo = RepoWithParserBranch();
        Assert.Equal(ExitCodes.Ok, Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo)).Code);
        var landed = repo.Sha("epic/E1");
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo));
        Assert.Equal(ExitCodes.Ok, code);
        var json = SingleJsonLine(output);
        Assert.True(json.GetProperty("empty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("commit").ValueKind);
        Assert.Equal(landed, repo.Sha("epic/E1"));
    }

    [Fact]
    public void Run_FeaturePrefixedEpicBranch()
    {
        using var repo = TempRepo.Create();
        repo.Epic(name: "feature/9933-squash-tool");
        repo.Branch("task/9933-cli", "feature/9933-squash-tool", ("cli.txt", "c\n"));
        var config = Config(repo, c => c with { Epic = "9933-squash-tool", EpicBranchTemplate = "feature/{epic}" });
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/9933-cli", "--config", config);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("feature/9933-squash-tool", SingleJsonLine(output).GetProperty("epicBranch").GetString());
        Assert.Equal("9933-squash-tool", Trailers(repo, "feature/9933-squash-tool", "Epic"));
        Assert.True(repo.HasFile("feature/9933-squash-tool", "cli.txt"));
    }

    [Fact]
    public void Run_Conflict_Exit1_NothingMoves()
    {
        using var repo = TempRepo.Create();
        repo.Commit("shared", ("shared.txt", "base\n"));
        repo.Epic();
        var branchTip = repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic edits shared", ("shared.txt", "epic\n"));
        repo.Git("checkout", "-q", "main");
        var before = repo.Sha("epic/E1");
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/T1", "--config", Config(repo));
        Assert.Equal(ExitCodes.Returned, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("shared.txt", json.GetProperty("note").GetString());
        Assert.Equal(before, repo.Sha("epic/E1"));
        Assert.Equal(branchTip, repo.Sha("task/T1"));
    }

    [Fact]
    public void Run_RequireTicket_Exit1_UntilTicketGiven()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var config = Config(repo, c => c with { Squash = new SquashConfig { RequireTicket = true } });
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/T1", "--config", config);
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Contains("no ticket", SingleJsonLine(output).GetProperty("note").GetString());
        var (code2, output2, _) = Run(repo, "run", "--task", "T1", "--branch", "task/T1", "--ticket", "77", "--config", config);
        Assert.Equal(ExitCodes.Ok, code2);
        Assert.Equal("77", SingleJsonLine(output2).GetProperty("ticket").GetString());
    }

    [Fact]
    public void Run_RequireTicket_TicketlessNoOp_IsEmptyWithNullTicket()
    {
        // The branch adds nothing to the epic: no commit is made, so no ticket is needed (Ruling B3-final).
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Git("branch", "task/T1", "epic/E1");
        var before = repo.Sha("epic/E1");
        var config = Config(repo, c => c with { Squash = new SquashConfig { RequireTicket = true } });
        var (code, output, _) = Run(repo, "run", "--task", "T1", "--branch", "task/T1", "--config", config);
        Assert.Equal(ExitCodes.Ok, code);
        var json = SingleJsonLine(output);
        Assert.True(json.GetProperty("empty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("ticket").ValueKind);
        Assert.Equal(before, repo.Sha("epic/E1"));
    }

    [Fact]
    public void Run_MissingBranch_Exit3()
    {
        using var repo = RepoWithParserBranch();
        var (code, output, err) = Run(repo, "run", "--task", "T1", "--branch", "task/nope", "--config", Config(repo));
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Empty(output);
        Assert.Equal("error: task branch 'task/nope' not found", err.TrimEnd());
    }

    [Fact]
    public void Run_MissingEpic_Exit3()
    {
        using var repo = TempRepo.Create();
        repo.Branch("task/T1", "main", ("one.txt", "1\n"));
        var (code, _, err) = Run(repo, "run", "--task", "T1", "--branch", "task/T1", "--config", Config(repo));
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic branch 'epic/E1' not found", err);
    }

    [Theory]
    [InlineData("--task", "T 1")]
    [InlineData("--ticket", "two words")]
    [InlineData("--run-id", "../x")]
    public void Run_BadValues_Exit2(string option, string value)
    {
        using var repo = RepoWithParserBranch();
        string[] args = option == "--task"
            ? ["run", "--task", value, "--branch", "task/9933-parser", "--config", Config(repo)]
            : ["run", "--task", "T1", "--branch", "task/9933-parser", option, value, "--config", Config(repo)];
        var (code, output, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.StartsWith("error: ", err.TrimEnd());
    }

    [Fact]
    public void EpicLockHeld_Exit4()
    {
        using var repo = RepoWithParserBranch();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).BatchLockDir("E1"), SlotOptions.From(config) with { Slots = 1 }).Acquire("batch run");
        var before = repo.Sha("epic/E1");
        var (code, output, err) = Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", TestConfig.Write(repo, config));
        Assert.Equal(ExitCodes.Environment, code);
        Assert.Empty(output);
        Assert.StartsWith("error: another batch or squash run holds epic 'E1'", err.TrimEnd());
        Assert.Equal(before, repo.Sha("epic/E1"));
    }

    [Fact]
    public void NoArguments_Exit2OneLine()
    {
        // The renderer sample's runbook step is the bare `dnx Swarm.Squash@0.1.0`.
        using var repo = RepoWithParserBranch();
        var before = repo.Sha("epic/E1");
        var (code, output, err) = Run(repo);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.StartsWith("error: ", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
        Assert.Equal(before, repo.Sha("epic/E1"));
    }

    [Fact]
    public void MissingRequiredOption_Exit2()
    {
        using var repo = RepoWithParserBranch();
        var (code, _, err) = Run(repo, "run", "--task", "T1");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.StartsWith("error: ", err.TrimEnd());
    }

    [Fact]
    public void EpicMovedDuringEmptyRun_Exit4()
    {
        using var repo = RepoWithParserBranch();
        Assert.Equal(ExitCodes.Ok, Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo)).Code);
        var config = TestConfig.For(repo);
        var context = new ToolContext(RepoLocator.Locate(repo.Root), config, new StateLayout(repo.StateDir), Verbosity.Quiet);
        var runner = new SquashRunner(context, new Progress(new StringWriter(), Verbosity.Quiet))
        {
            // Simulates a concurrent epic move after the (empty) land made no update-ref.
            AfterLand = () => repo.Git("update-ref", "refs/heads/epic/E1", repo.Sha("main")),
        };
        var ex = Assert.Throws<ToolException>(() => runner.Run(new SquashRunRequest("T1", "task/9933-parser", null, null)));
        Assert.Equal(ExitCodes.Environment, ex.ExitCode);
        Assert.Contains("moved during the run", ex.Message);
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void ResetFailure_DoesNotMaskTheOriginalError()
    {
        using var repo = RepoWithParserBranch();
        Assert.Equal(ExitCodes.Ok, Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo)).Code);
        var stderr = new StringWriter();
        var runner = Runner(repo, stderr, () =>
        {
            repo.Git("update-ref", "refs/heads/epic/E1", repo.Sha("main"));
            LockIntegrationIndex(repo);
        });
        var ex = Assert.Throws<ToolException>(() => runner.Run(new SquashRunRequest("T1", "task/9933-parser", null, null)));
        Assert.Contains("moved during the run", ex.Message);
        Assert.Contains("warning: could not reset the integration worktree", stderr.ToString());
    }

    [Fact]
    public void ResetFailure_AfterLanding_IsOnlyAWarning()
    {
        using var repo = RepoWithParserBranch();
        var before = repo.Sha("epic/E1");
        var stderr = new StringWriter();
        var result = Runner(repo, stderr, () => LockIntegrationIndex(repo)).Run(new SquashRunRequest("T1", "task/9933-parser", null, null));
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(repo.Sha("epic/E1"), result.Commit);
        Assert.Equal(before, repo.Sha("epic/E1~1"));
        var warning = Assert.Single(stderr.ToString().Split('\n'), l => l.StartsWith("warning: ", StringComparison.Ordinal));
        Assert.Contains("could not reset the integration worktree", warning);
    }

    [Fact]
    public void Run_EpicFlagOverridesConfig()
    {
        using var repo = RepoWithParserBranch();
        var (code, output, err) = Run(repo, "run", "--task", "T1", "--branch", "task/9933-parser", "--config", Config(repo), "--epic", "E9");
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic branch 'epic/E9' not found", err);
        Assert.Equal("", output);
    }

    static SquashRunner Runner(TempRepo repo, TextWriter stderr, Action afterLand)
    {
        var context = new ToolContext(RepoLocator.Locate(repo.Root), TestConfig.For(repo), new StateLayout(repo.StateDir), Verbosity.Quiet);
        return new SquashRunner(context, new Progress(stderr, Verbosity.Quiet)) { AfterLand = afterLand };
    }

    // A held index.lock makes the integration worktree's `git checkout` (ResetTo) fail.
    static void LockIntegrationIndex(TempRepo repo)
    {
        var gitDir = TempRepo.RunGit(Path.Combine(repo.WorktreeRoot, "int-E1"), "rev-parse", "--absolute-git-dir");
        File.WriteAllText(Path.Combine(gitDir, "index.lock"), "");
    }
}
