using Swarm.Batching;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class TouchIndexTests
{
    static TaskUnit U(string id) => new([new TaskSpec(id, "task/" + id, [])]);

    [Fact]
    public void Derive_ListsBranchChangesSinceMergeBase()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("src/a.cs", "a\n"), ("b.txt", "b\n"));
        var touches = new TouchIndex(new GitRunner(repo.Root));
        Assert.Equal(new[] { "b.txt", "src/a.cs" }, touches.Derive("refs/heads/epic/E1", "refs/heads/task/T1").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Overlap_IsCaseInsensitive()
    {
        using var repo = TempRepo.Create();
        var touches = new TouchIndex(new GitRunner(repo.Root));
        touches.Set("T1", new HashSet<string>(["Src/Foo.cs"], StringComparer.OrdinalIgnoreCase));
        touches.Set("T2", new HashSet<string>(["src/foo.cs"], StringComparer.OrdinalIgnoreCase));
        Assert.True(touches.Overlaps(U("T1"), U("T2")));
        Assert.Equal(new[] { "T1" }, touches.Partners(["T1", "T1", "T3"], ["SRC/FOO.CS"]));
    }

    [Fact]
    public void NonAsciiNames_AreUnquoted()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("ü.txt", "x\n"));
        Assert.Contains("ü.txt", new TouchIndex(new GitRunner(repo.Root)).Derive("refs/heads/epic/E1", "refs/heads/task/T1"));
    }

    [Fact]
    public void Rename_ReportsOldAndNewPath()
    {
        using var repo = TempRepo.Create();
        repo.Commit("foo", ("Foo.cs", "class Foo { /* some content to match */ }\n"));
        repo.Epic();
        repo.Git("checkout", "-q", "-b", "task/T1", "epic/E1");
        repo.Git("mv", "Foo.cs", "Bar.cs");
        repo.Git("commit", "-q", "-m", "rename");
        repo.Git("checkout", "-q", "main");
        var touches = new TouchIndex(new GitRunner(repo.Root));
        var files = touches.Derive("refs/heads/epic/E1", "refs/heads/task/T1");
        Assert.Equal(new[] { "Bar.cs", "Foo.cs" }, files.Order(StringComparer.Ordinal));
        touches.Set("T1", files);
        Assert.Equal(new[] { "T1" }, touches.Partners(["T1"], ["Foo.cs"]));
    }

    [Fact]
    public void UnusualNames_AreReturnedVerbatim()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", (" lead ü.txt", "x\n"), ("a b/c d.txt", "y\n"));
        var files = new TouchIndex(new GitRunner(repo.Root)).Derive("refs/heads/epic/E1", "refs/heads/task/T1");
        Assert.Equal(new[] { " lead ü.txt", "a b/c d.txt" }, files.Order(StringComparer.Ordinal));
    }
}
