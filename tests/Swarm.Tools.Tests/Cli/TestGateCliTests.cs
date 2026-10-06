using System.Text.Json;
using System.Text.RegularExpressions;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using TestGateProgram = Swarm.TestGate.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class TestGateCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = TestGateProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static string Config(TempRepo repo) => TestConfig.Write(repo, TestConfig.For(repo));

    [Fact]
    public void Run_GreenCommand_PrintsOneJsonLineAndExits0()
    {
        using var repo = TempRepo.Create();
        var (code, output, err) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command()]);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("fake-suite: green", err);
        Assert.True(File.Exists(new StateLayout(repo.StateDir).GateEventsFile));
    }

    [Fact]
    public void Run_RedCommand_Exits1WithChildCodeInJson()
    {
        using var repo = TempRepo.Create();
        File.WriteAllText(Path.Combine(repo.Root, "x.fail"), "");
        var (code, output, _) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command()]);
        Assert.Equal(1, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Run_NoCommand_UsesConfigTestCommand()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "run", "--config", Config(repo));
        Assert.Equal(0, code);
        Assert.Equal(0, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void AtSignArgument_IsPassedThrough()
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command("@notafile")]);
        Assert.Equal(0, code);
        Assert.Contains("@notafile", err);
    }

    [Fact]
    public void LongStatePath_Exits2OneLine()
    {
        using var repo = TempRepo.Create();
        var (code, output, err) = Run(repo, "run", "--config", Config(repo), "--state", Path.Combine(repo.Sandbox, new string('x', 220)), "--", "git", "--version");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Matches(new Regex(@"^error: state dir path is \d+ chars \(limit 200\)"), err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void MaxWaitTimeout_Exits5()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("holder");
        var (code, output, err) = Run(repo, ["run", "--config", TestConfig.Write(repo, config), "--max-wait", "1", "--", .. FakeSuite.Command()]);
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Empty(output);
        Assert.StartsWith("error: no test slot free after", err.TrimEnd());
    }

    [Fact]
    public void Status_ListsHolder()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("holder");
        var (code, output, _) = Run(repo, "status", "--config", TestConfig.Write(repo, config));
        Assert.Equal(0, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("holders").GetArrayLength());
    }

    [Fact]
    public void Reclaim_WithoutForce_Exits2()
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, "reclaim", "--config", Config(repo));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal("error: reclaim deletes lock files (pass --force to confirm)", err.TrimEnd());
    }

    [Fact]
    public void Reclaim_WithForce_PrintsReport()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "reclaim", "--force", "--config", Config(repo));
        Assert.Equal(0, code);
        Assert.Equal(0, SingleJsonLine(output).GetProperty("reclaimed").GetArrayLength());
    }

    [Fact]
    public void Run_OptionLikeFirstToken_IsUsage()
    {
        using var repo = TempRepo.Create();
        var (code, output, err) = Run(repo, "run", "--config", Config(repo), "--bogus");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Contains("put the command after --", err);
    }

    [Theory]
    [InlineData("status", "--bogus")]
    [InlineData]
    [InlineData("nope")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.StartsWith("error: ", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void UnknownCommand_ReportsUnrecognizedNotRequiredCommand()
    {
        using var repo = TempRepo.Create();
        var (_, _, err) = Run(repo, "nope");
        Assert.Contains("Unrecognized", err);
    }

    [Fact]
    public void OutsideRepo_Exits3()
    {
        using var dir = new TempDir();
        var stderr = new StringWriter();
        Assert.Equal(ExitCodes.BadInput, TestGateProgram.Run(["status"], new StringWriter(), stderr, dir.Dir));
    }
}
