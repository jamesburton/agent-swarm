using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class BranchMetaTests
{
    [Fact]
    public void WriteThenReadAll_RoundTripsBranchWithSlashes()
    {
        using var repo = TempRepo.Create();
        repo.Epic(name: "epic/42-auth");
        repo.Branch("feature/9933-login", "epic/42-auth", ("a.txt", "a\n"));
        var git = new GitRunner(repo.Root);
        var meta = new BranchMeta("feature/9933-login", "9933", "epic/42-auth", repo.Sha("epic/42-auth"));
        BranchMetaStore.Write(git, meta);
        Assert.Equal(meta, BranchMetaStore.ReadAll(git)["feature/9933-login"]);
    }

    [Fact]
    public void NoMetadata_IsEmpty()
    {
        using var repo = TempRepo.Create();
        Assert.Empty(BranchMetaStore.ReadAll(new GitRunner(repo.Root)));
    }

    [Fact]
    public void DeletingTheBranch_RemovesItsMetadata()
    {
        using var repo = TempRepo.Create();
        repo.Branch("task/1-x", "main", ("a.txt", "a\n"));
        var git = new GitRunner(repo.Root);
        BranchMetaStore.Write(git, new BranchMeta("task/1-x", "1", "main", repo.Sha("main")));
        repo.Git("branch", "-D", "task/1-x");
        Assert.Empty(BranchMetaStore.ReadAll(git));
    }
}
