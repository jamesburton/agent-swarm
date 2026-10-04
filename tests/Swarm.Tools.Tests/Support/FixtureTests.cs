using System.Diagnostics;

namespace Swarm.Tools.Tests.Support;

public class FixtureTests
{
    static (int Code, string Out) RunFake(string cwd, params string[] extra)
    {
        var cmd = FakeSuite.Command(extra);
        var psi = new ProcessStartInfo(cmd[0]) { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in cmd.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void FakeSuite_GreenByDefault()
    {
        using var dir = new TempDir();
        var (code, output) = RunFake(dir.Dir);
        Assert.Equal(0, code);
        Assert.Contains("fake-suite: green", output);
    }

    [Fact]
    public void FakeSuite_EmptyFailFileIsRed()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "T3.fail"), "");
        var (code, output) = RunFake(dir.Dir);
        Assert.Equal(1, code);
        Assert.Contains("fake-suite: red (T3.fail)", output);
    }

    [Fact]
    public void FakeSuite_InteractionRuleNeedsAllFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "pair.fail"), "t3.txt\n");
        Assert.Equal(0, RunFake(dir.Dir).Code);
        File.WriteAllText(Path.Combine(dir.Dir, "t3.txt"), "x");
        Assert.Equal(1, RunFake(dir.Dir).Code);
    }

    [Fact]
    public void TempRepo_BranchIsNotOnMain()
    {
        using var repo = TempRepo.Create();
        repo.Branch("task/T1", "main", ("one.txt", "one\n"));
        Assert.True(repo.HasFile("task/T1", "one.txt"));
        Assert.False(repo.HasFile("main", "one.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "one.txt")));
    }

    [Fact]
    public void TempRepo_WriteTasksIsCamelCaseJson()
    {
        using var repo = TempRepo.Create();
        var text = File.ReadAllText(repo.WriteTasks(new TaskLine("T2", "task/T2", ["T1"])));
        Assert.Contains("\"dependsOn\":[\"T1\"]", text);
    }

    [Fact]
    public void TempRepo_DisposeDeletesSandboxIncludingGitObjects()
    {
        var repo = TempRepo.Create();
        var sandbox = repo.Sandbox;
        repo.Dispose();
        Assert.False(Directory.Exists(sandbox));
    }
}
