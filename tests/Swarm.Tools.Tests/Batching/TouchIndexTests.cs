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
}
