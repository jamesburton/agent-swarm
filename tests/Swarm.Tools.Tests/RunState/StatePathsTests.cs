using System.Text.RegularExpressions;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class StatePathsTests
{
    static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    static string AddLinkedWorktree(TempRepo repo)
    {
        var side = Path.Combine(repo.Sandbox, "side");
        repo.Git("worktree", "add", "-q", "-b", "side", side);
        return side;
    }

    [Fact]
    public void Locate_FromMainWorktree()
    {
        using var repo = TempRepo.Create();
        var paths = RepoLocator.Locate(repo.Root);
        Assert.True(Same(repo.Root, paths.MainWorktreeRoot));
        Assert.True(Same(repo.Root, paths.WorktreeRoot));
    }

    [Fact]
    public void Locate_FromLinkedWorktree_FindsMain()
    {
        using var repo = TempRepo.Create();
        var side = AddLinkedWorktree(repo);
        var paths = RepoLocator.Locate(side);
        Assert.True(Same(repo.Root, paths.MainWorktreeRoot));
        Assert.True(Same(side, paths.WorktreeRoot));
    }

    [Fact]
    public void Locate_FromSubdirectory()
    {
        using var repo = TempRepo.Create();
        var sub = Path.Combine(repo.Root, "a", "b");
        Directory.CreateDirectory(sub);
        Assert.True(Same(repo.Root, RepoLocator.Locate(sub).MainWorktreeRoot));
    }

    [Fact]
    public void Locate_OutsideRepo_IsBadInput()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => RepoLocator.Locate(dir.Dir)).ExitCode);
    }

    [Fact]
    public void StateDir_RelativeResolvesUnderMainWorktree_EvenFromLinkedWorktree()
    {
        using var repo = TempRepo.Create();
        var paths = RepoLocator.Locate(AddLinkedWorktree(repo));
        Assert.True(Same(Path.Combine(repo.Root, ".docs", "runs"), StatePaths.Resolve(paths, ".docs/runs")));
    }

    [Fact]
    public void StateDir_AbsoluteIsKept()
    {
        using var repo = TempRepo.Create();
        Assert.True(Same(repo.StateDir, StatePaths.Resolve(RepoLocator.Locate(repo.Root), repo.StateDir)));
    }

    [Fact]
    public void TooLongStateDir_Exit2OneLine()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => StatePaths.Resolve(RepoLocator.Locate(repo.Root), Path.Combine(repo.Sandbox, new string('x', 220))));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Matches(new Regex(@"^error: state dir path is \d+ chars \(limit 200\)"), e.ErrorLine);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void WorktreeRoot_DefaultsToSiblingOfMainWorktree()
    {
        using var repo = TempRepo.Create();
        var root = StatePaths.ResolveWorktreeRoot(RepoLocator.Locate(repo.Root), null);
        Assert.True(Same(Path.Combine(repo.Sandbox, "repo-wt"), root));
    }

    [Fact]
    public void Layout_Paths()
    {
        var layout = new StateLayout(Path.Combine("C:", "s"));
        Assert.Equal(Path.Combine("C:", "s", "slots"), layout.SlotsDir);
        Assert.Equal(Path.Combine("C:", "s", "locks", "batch-E1"), layout.BatchLockDir("E1"));
        Assert.Equal(Path.Combine("C:", "s", "runs", "r1"), layout.RunDir("r1"));
        Assert.Equal(Path.Combine("C:", "s", "testgate.events.jsonl"), layout.GateEventsFile);
    }
}
