using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class WorktreeManagerTests
{
    const string Epic = "epic/42-auth";

    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic(name: Epic);
        return repo;
    }

    static WorktreeManager Manager(TempRepo repo, Func<SwarmConfig, SwarmConfig>? tweak = null)
    {
        var config = TestConfig.For(repo);
        return new WorktreeManager(RepoLocator.Locate(repo.Root), tweak?.Invoke(config) ?? config);
    }

    static SwarmConfig CiNaming(SwarmConfig c) => c with
    {
        Worktree = new WorktreeSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
    };

    [Fact]
    public void Create_BranchesFromEpicAndRecordsMetadata()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var r = m.Create(new CreateRequest("9933", "login-form", Epic, null));
        Assert.True(r.Created);
        Assert.Equal("task/9933-login-form", r.Branch);
        Assert.True(WorktreeList.SamePath(Path.Combine(repo.WorktreeRoot, "t-9933"), r.Path));
        Assert.Equal(repo.Sha(Epic), r.Head);
        Assert.Equal(new BranchMeta("task/9933-login-form", "9933", Epic, repo.Sha(Epic)), BranchMetaStore.ReadAll(m.Git)["task/9933-login-form"]);
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public void Create_IsIdempotentByBranch()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var first = m.Create(new CreateRequest("9933", "login-form", Epic, null));
        var again = m.Create(new CreateRequest("9933", "login-form", Epic, null));
        Assert.False(again.Created);
        Assert.True(WorktreeList.SamePath(first.Path, again.Path));
    }

    [Fact]
    public void Create_SameBranchOtherBase_IsBadInput()
    {
        using var repo = Repo();
        repo.Epic(name: "epic/7-other");
        var m = Manager(repo);
        m.Create(new CreateRequest("9933", "login-form", Epic, null));
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => m.Create(new CreateRequest("9933", "login-form", "epic/7-other", null))).ExitCode);
    }

    [Fact]
    public void CiTemplate_CreatesFeatureBranch()
    {
        using var repo = Repo();
        Assert.Equal("feature/9933-login-form", Manager(repo, CiNaming).Create(new CreateRequest("9933", "login-form", Epic, null)).Branch);
    }

    [Fact]
    public void DisallowedKind_CreatesNothing()
    {
        using var repo = Repo();
        var e = Assert.Throws<ToolException>(() => Manager(repo, CiNaming).Create(new CreateRequest("9933", "login-form", Epic, "fix")));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.False(Directory.Exists(repo.WorktreeRoot));
        Assert.Empty(repo.Git("branch", "--list", "fix/*"));
    }

    [Fact]
    public void MissingBase_IsBadInput()
    {
        using var repo = Repo();
        var e = Assert.Throws<ToolException>(() => Manager(repo).Create(new CreateRequest("1", "x", "epic/nope", null)));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("base branch 'epic/nope' not found", e.Message);
    }

    [Fact]
    public void ExistingBranchWithoutWorktree_IsBadInput()
    {
        using var repo = Repo();
        repo.Git("branch", "task/1-x", Epic);
        Assert.Contains("already exists", Assert.Throws<ToolException>(() => Manager(repo).Create(new CreateRequest("1", "x", Epic, null))).Message);
    }

    [Fact]
    public void LongRoot_Exit2_NothingCreated()
    {
        using var repo = Repo();
        var root = Path.Combine(repo.Sandbox, new string('w', 220));
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => Manager(repo, c => c with { WorktreeRoot = root }).Create(new CreateRequest("1", "x", Epic, null))).ExitCode);
        Assert.Empty(repo.Git("branch", "--list", "task/*"));
    }

    [Fact]
    public void ForeignNonEmptyDirectory_IsEnvironment()
    {
        using var repo = Repo();
        var path = Path.Combine(repo.WorktreeRoot, "t-1");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mine.txt"), "x");
        Assert.Equal(ExitCodes.Environment, Assert.Throws<ToolException>(() => Manager(repo).Create(new CreateRequest("1", "x", Epic, null))).ExitCode);
        Assert.Empty(repo.Git("branch", "--list", "task/*"));
    }

    [Fact]
    public void LockedMissingRegistrationAtPath_IsBadInput()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var r = m.Create(new CreateRequest("1", "x", Epic, null));
        repo.Git("worktree", "lock", "--reason", "usb disk", r.Path);
        FileTree.DeleteTree(r.Path);

        // Same ticket, other slug: same path t-1, new branch, so only the locked registration is in the way.
        var e = Assert.Throws<ToolException>(() => m.Create(new CreateRequest("1", "y", Epic, null)));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("locked", e.Message);
    }

    [Fact]
    public void DeepTrackedFile_WarnsAboutMaxPath()
    {
        using var repo = Repo();
        repo.Git("checkout", "-q", Epic);
        repo.Commit("deep", ("src/" + new string('d', 120) + ".cs", "x\n"));
        repo.Git("checkout", "-q", "main");
        var root = Path.Combine(repo.Sandbox, new string('r', 120));
        var r = Manager(repo, c => c with { WorktreeRoot = root }).Create(new CreateRequest("1", "x", Epic, null));
        Assert.Contains(r.Warnings, w => w.Contains("> 259", StringComparison.Ordinal));
    }

    [Fact]
    public void List_ReportsStatesOfManagedWorktrees()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var empty = m.Create(new CreateRequest("1", "empty", Epic, null));
        var dirty = m.Create(new CreateRequest("2", "dirty", Epic, null));
        var ahead = m.Create(new CreateRequest("3", "ahead", Epic, null));
        var locked = m.Create(new CreateRequest("4", "locked", Epic, null));
        File.WriteAllText(Path.Combine(dirty.Path, "new.txt"), "untracked\n");
        File.WriteAllText(Path.Combine(ahead.Path, "work.txt"), "work\n");
        TempRepo.RunGit(ahead.Path, "add", "-A");
        TempRepo.RunGit(ahead.Path, "commit", "-q", "-m", "work");
        repo.Git("worktree", "lock", "--reason", "agent busy", locked.Path);

        var list = m.List().ToDictionary(e => e.Ticket!);
        Assert.Equal(4, list.Count);
        Assert.True(list["1"].Empty);
        Assert.Null(list["1"].MergedVia);
        Assert.True(list["2"].Dirty);
        Assert.Equal(1, list["3"].AheadOfBase);
        Assert.Null(list["3"].MergedVia);
        Assert.Equal(("agent busy", true), (list["4"].LockReason, list["4"].Locked));
        Assert.All(list.Values, e => Assert.True(e.Managed && e.BaseExists));
    }

    [Fact]
    public void List_ExcludesUnmanagedUnlessAll_AndFiltersByBase()
    {
        using var repo = Repo();
        repo.Epic(name: "epic/7-other");
        var m = Manager(repo);
        m.Create(new CreateRequest("1", "x", Epic, null));
        m.Create(new CreateRequest("2", "y", "epic/7-other", null));
        repo.Git("worktree", "add", "-q", "-b", "mine", Path.Combine(repo.Sandbox, "mine"));
        Assert.Equal(2, m.List().Count);
        Assert.Equal("1", Assert.Single(m.List(Epic)).Ticket);
        var all = m.List(all: true);
        Assert.Contains(all, e => e.Branch == "mine" && !e.Managed);
        Assert.Contains(all, e => e.Branch == "main" && !e.Managed);
    }

    [Fact]
    public void List_MissingDirectory_IsReported()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var r = m.Create(new CreateRequest("1", "x", Epic, null));
        FileTree.DeleteTree(r.Path);
        var e = Assert.Single(m.List());
        Assert.True(e.Missing);
        Assert.False(e.Dirty);
    }

    static void WaitForWorktree(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(path, ".git")) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Create_RetriesMetadataWriteWhileConfigIsLocked()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var gate = Path.Combine(repo.Root, ".git", "config.lock");
        File.WriteAllText(gate, string.Empty);

        // Release only well after the worktree appears (so the first metadata write, which follows git's checkout, meets the lock), so a later attempt succeeds.
        var release = new Thread(() =>
        {
            WaitForWorktree(m.PathFor("1"));
            Thread.Sleep(330);
            File.Delete(gate);
        });
        release.Start();
        var r = m.Create(new CreateRequest("1", "x", Epic, null));
        release.Join();
        Assert.True(r.Created);
        Assert.Equal("1", BranchMetaStore.ReadAll(m.Git)[r.Branch].Ticket);
    }

    [Fact]
    public void Create_MetadataWriteFailsAndFileIsHeld_ReportsDirectoryLeftBehindTruthfully()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var path = m.PathFor("1");
        var gate = Path.Combine(repo.Root, ".git", "config.lock");
        File.WriteAllText(gate, string.Empty);
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            WaitForWorktree(path);
            using var f = new FileStream(Path.Combine(path, "held.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            held.Wait();
        });
        holder.Start();
        try
        {
            var e = Assert.Throws<ToolException>(() => m.Create(new CreateRequest("1", "x", Epic, null)));
            Assert.Equal(ExitCodes.Environment, e.ExitCode);
            Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal);
            Assert.Contains("left behind", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("worktree and branch removed", e.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            held.Set();
            holder.Join();
            File.Delete(gate);
        }
    }

    [Fact]
    public void Create_WorktreeAddFails_RemovesBranchSoRetrySucceeds()
    {
        using var repo = Repo();
        repo.Epic(name: "epic/bad");

        // A tracked path that Windows git refuses to check out (':' is protected under core.protectNTFS); the tree is built by hand as <mode> <name> NUL <raw sha>.
        var blob = Path.Combine(repo.Sandbox, "blob.txt");
        File.WriteAllText(blob, "x\n");
        var sha = repo.Git("hash-object", "-w", blob);
        var treeFile = Path.Combine(repo.Sandbox, "tree.bin");
        File.WriteAllBytes(treeFile, [.. System.Text.Encoding.ASCII.GetBytes("100644 a:b.txt "), .. Convert.FromHexString(sha)]);
        var tree = repo.Git("hash-object", "-t", "tree", "-w", "--literally", treeFile);
        var commit = repo.Git("commit-tree", tree, "-p", "epic/bad", "-m", "bad path");
        repo.Git("update-ref", "refs/heads/epic/bad", commit);

        var m = Manager(repo);
        var e = Assert.Throws<ToolException>(() => m.Create(new CreateRequest("1", "x", "epic/bad", null)));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal);
        Assert.Empty(repo.Git("branch", "--list", "task/*"));
        Assert.Single(WorktreeList.Read(m.Git));
        Assert.True(m.Create(new CreateRequest("1", "x", Epic, null)).Created);
    }

    [Fact]
    public void Create_BranchAppearsBeforeAdd_ForeignBranchIsLeftUntouched()
    {
        using var repo = Repo();
        var foreign = repo.Commit("elsewhere", ("other.txt", "y\n"));
        var m = Manager(repo);

        // Simulates another process creating the branch (at a different commit) after the existence check.
        m.BeforeWorktreeAdd = b => repo.Git("branch", b, foreign);
        var e = Assert.Throws<ToolException>(() => m.Create(new CreateRequest("1", "x", Epic, null)));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal);
        Assert.Contains("left untouched", e.Message, StringComparison.Ordinal);
        Assert.Equal(foreign, repo.Sha("task/1-x"));
    }

    [Fact]
    public void Create_MetadataWriteKeepsFailing_RollsBackAndExits4()
    {
        using var repo = Repo();
        var m = Manager(repo);
        var gate = Path.Combine(repo.Root, ".git", "config.lock");
        File.WriteAllText(gate, string.Empty);
        try
        {
            var e = Assert.Throws<ToolException>(() => m.Create(new CreateRequest("1", "x", Epic, null)));
            Assert.Equal(ExitCodes.Environment, e.ExitCode);
            Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(gate);
        }

        Assert.Empty(repo.Git("branch", "--list", "task/*"));
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, "t-1")));
        Assert.Single(WorktreeList.Read(m.Git));
        Assert.Empty(BranchMetaStore.ReadAll(m.Git));
    }
}
