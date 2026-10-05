using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class MergeCheckTests
{
    static readonly IReadOnlySet<string> NoLedger = new HashSet<string>();

    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic(name: "epic/42-auth");
        return repo;
    }

    static void OnEpic(TempRepo repo, string message, params (string Path, string Content)[] files)
    {
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Commit(message, files);
        repo.Git("checkout", "-q", "main");
    }

    [Fact]
    public void Unmerged_IsNull()
    {
        using var repo = Repo();
        repo.Branch("task/1-x", "epic/42-auth", ("a.txt", "a\n"));
        Assert.Null(MergeCheck.LandedVia(new GitRunner(repo.Root), "task/1-x", "epic/42-auth", NoLedger));
    }

    [Fact]
    public void MergedBranch_IsMergedViaAncestor()
    {
        using var repo = Repo();
        repo.Branch("task/1-x", "epic/42-auth", ("a.txt", "a\n"));
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Git("merge", "-q", "--no-ff", "--no-edit", "task/1-x");
        repo.Git("checkout", "-q", "main");
        Assert.Equal(MergeVia.Ancestor, MergeCheck.LandedVia(new GitRunner(repo.Root), "task/1-x", "epic/42-auth", NoLedger));
    }

    [Fact]
    public void SquashedContent_IsMergedViaContent()
    {
        using var repo = Repo();
        repo.Git("checkout", "-q", "-b", "task/1-x", "epic/42-auth");
        repo.Commit("one", ("a.txt", "a\n"));
        repo.Commit("two", ("b.txt", "b\n"));
        repo.Git("checkout", "-q", "main");
        OnEpic(repo, "9933: squashed\n\nTicket: 9933", ("a.txt", "a\n"), ("b.txt", "b\n"));
        OnEpic(repo, "later epic work", ("c.txt", "c\n"));
        Assert.Equal(MergeVia.Content, MergeCheck.LandedVia(new GitRunner(repo.Root), "task/1-x", "epic/42-auth", NoLedger));
    }

    [Fact]
    public void PartiallyLanded_IsNotMerged()
    {
        using var repo = Repo();
        repo.Branch("task/1-x", "epic/42-auth", ("a.txt", "a\n"), ("b.txt", "b\n"));
        OnEpic(repo, "only half", ("a.txt", "a\n"));
        Assert.Null(MergeCheck.LandedVia(new GitRunner(repo.Root), "task/1-x", "epic/42-auth", NoLedger));
    }

    [Fact]
    public void LedgerBranch_IsMergedViaLedger()
    {
        using var repo = Repo();
        repo.Branch("task/1-x", "epic/42-auth", ("a.txt", "a\n"));
        Assert.Equal(MergeVia.Ledger, MergeCheck.LandedVia(new GitRunner(repo.Root), "task/1-x", "epic/42-auth", new HashSet<string> { "task/1-x" }));
    }
}
