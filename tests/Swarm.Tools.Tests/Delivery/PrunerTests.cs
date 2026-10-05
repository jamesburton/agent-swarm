using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class PrunerTests
{
    const string Epic = "epic/42-auth";

    static WorktreeEntry Entry(bool locked = false, bool missing = false, bool dirty = false, bool empty = false, string? merged = null, bool baseExists = true, bool managed = true, string? error = null, bool? directoryExists = null) =>
        new("p", "task/1-x", "1", Epic, baseExists, "h", locked, locked ? "busy" : null, missing, dirty, empty, empty ? 0 : 2, merged, managed, error, directoryExists ?? !missing);

    // Deletes a worktree's .git file and leaves uncommitted work: git then lists the registration as prunable though the directory exists.
    static string StaleGitdirWithWork(string worktree)
    {
        var work = Path.Combine(worktree, "work.txt");
        File.WriteAllText(work, "only copy\n");
        File.Delete(Path.Combine(worktree, ".git"));
        return work;
    }

    static (TempRepo Repo, WorktreeManager Manager) Setup()
    {
        var repo = TempRepo.Create();
        repo.Epic(name: Epic);
        return (repo, new WorktreeManager(RepoLocator.Locate(repo.Root), TestConfig.For(repo)));
    }

    static WorktreeCreateResult Work(WorktreeManager m, string ticket)
    {
        var r = m.Create(new CreateRequest(ticket, "x", Epic, null));
        File.WriteAllText(Path.Combine(r.Path, $"f{ticket}.txt"), ticket + "\n");
        TempRepo.RunGit(r.Path, "add", "-A");
        TempRepo.RunGit(r.Path, "commit", "-q", "-m", $"work {ticket}");
        return r;
    }

    static void SquashOntoEpic(TempRepo repo, string ticket)
    {
        repo.Git("checkout", "-q", Epic);
        repo.Commit($"{ticket}: squashed\n\nTicket: {ticket}", ($"f{ticket}.txt", ticket + "\n"));
        repo.Git("checkout", "-q", "main");
    }

    // Everything prune could change: registrations, refs, branch config (incl. swarm metadata), directories and their status.
    static string Snapshot(TempRepo repo, params string[] worktrees) =>
        string.Join(
            "\n",
            repo.Git("worktree", "list", "--porcelain"),
            repo.Git("for-each-ref", "--format=%(refname) %(objectname)"),
            repo.Git("config", "--get-regexp", @"^branch\."),
            string.Join("\n", worktrees.Select(w => $"{w} exists={Directory.Exists(w)} status={(Directory.Exists(w) ? TempRepo.RunGit(w, "status", "--porcelain") : "-")}")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_LockedIsAlwaysKept(bool force)
    {
        var d = Pruner.Decide(Entry(locked: true, merged: MergeVia.Ancestor), force);
        Assert.Equal(PruneActions.Keep, d.Action);
        Assert.Contains("locked (busy)", d.Reason);
    }

    [Fact]
    public void Decide_Table()
    {
        Assert.Equal((PruneActions.Remove, true), Pick(Pruner.Decide(Entry(merged: MergeVia.Content), false)));
        Assert.Equal((PruneActions.Keep, false), Pick(Pruner.Decide(Entry(dirty: true, merged: MergeVia.Content), false)));
        Assert.Equal((PruneActions.Keep, false), Pick(Pruner.Decide(Entry(empty: true), false)));
        Assert.Equal((PruneActions.Keep, false), Pick(Pruner.Decide(Entry(), false)));
        Assert.Equal((PruneActions.Remove, true), Pick(Pruner.Decide(Entry(dirty: true), true)));
        Assert.Equal((PruneActions.PruneMetadata, false), Pick(Pruner.Decide(Entry(missing: true), false)));
        Assert.Equal((PruneActions.PruneMetadata, true), Pick(Pruner.Decide(Entry(missing: true, merged: MergeVia.Ledger), false)));
        Assert.Contains("abandoned", Pruner.Decide(Entry(baseExists: false), false).Reason);
        Assert.StartsWith("forced: ", Pruner.Decide(Entry(), true).Reason);

        static (string, bool) Pick(PruneDecision d) => (d.Action, d.DeleteBranch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_UnassessableOrUnmanaged_IsAlwaysKept(bool force)
    {
        var unassessable = Pruner.Decide(Entry(merged: MergeVia.Ancestor, error: "status failed"), force);
        Assert.Equal((PruneActions.Keep, false), (unassessable.Action, unassessable.DeleteBranch));
        Assert.Equal("could not assess: status failed", unassessable.Reason);
        var unmanaged = Pruner.Decide(Entry(merged: MergeVia.Ancestor, managed: false), force);
        Assert.Equal((PruneActions.Keep, false), (unmanaged.Action, unmanaged.DeleteBranch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_StaleRegistrationWhoseDirectoryExists_IsKept(bool force)
    {
        var d = Pruner.Decide(Entry(missing: true, merged: MergeVia.Ancestor, directoryExists: true), force);
        Assert.Equal((PruneActions.Keep, false), (d.Action, d.DeleteBranch));
        Assert.Equal("registration stale (gitdir missing) but directory exists; inspect it", d.Reason);
    }

    [Fact]
    public void StaleRegistrationWithDirectory_KeptWithItsWorkEvenWithForce_RestProcessed()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var stale = Work(m, "1");
        var free = Work(m, "2");
        SquashOntoEpic(repo, "1");
        SquashOntoEpic(repo, "2");
        var work = StaleGitdirWithWork(stale.Path);
        var listed = m.List().Single(e => e.Ticket == "1");
        Assert.Equal((true, true), (listed.Missing, listed.DirectoryExists));

        var report = new Pruner(m).Prune(null, false, true);

        var item = report.Items.Single(i => i.Ticket == "1");
        Assert.Equal((PruneActions.Keep, false, false, null), (item.Action, item.Done, item.BranchDeleted, item.Error));
        Assert.Contains("inspect it", item.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("delete it manually", item.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(work));
        Assert.NotEmpty(repo.Git("branch", "--list", stale.Branch));
        Assert.Contains(WorktreeList.Read(m.Git), w => WorktreeList.SamePath(w.Path, stale.Path));
        Assert.False(Directory.Exists(free.Path));
        Assert.Equal((1, 1, 0), (report.Removed, report.Kept, report.Failed));
    }

    [Fact]
    public void RemoveRefusedForARegisteredDirectory_AdvisesInspectionNotDeletion_RestProcessed()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var first = Work(m, "1");
        var second = Work(m, "2");
        SquashOntoEpic(repo, "1");
        SquashOntoEpic(repo, "2");
        string? work = null;

        // The .git file disappears after listing, so git refuses the remove ("validation failed") and keeps the registration.
        var report = new Pruner(m) { BeforeApply = e => work ??= StaleGitdirWithWork(e.Path) }.Prune(null, false, false);

        var failed = report.Items.Single(i => i.Error is not null);
        Assert.Equal((false, false), (failed.Done, failed.BranchDeleted));
        Assert.Contains($"'{failed.Path}' still exists and is still registered; inspect it (it may hold uncommitted work)", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("delete it manually", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("in use", failed.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(work));
        Assert.NotEmpty(repo.Git("branch", "--list", failed.Branch));
        Assert.Equal((1, 1), (report.Removed, report.Failed));
        Assert.False(Directory.Exists(report.Items.Single(i => i.Error is null).Path));
        Assert.Contains(new[] { first.Path, second.Path }, p => WorktreeList.SamePath(p, failed.Path));
    }

    [Fact]
    public void NonGitExceptionOnOneItem_DoesNotStopTheRest()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var first = Work(m, "1");
        var second = Work(m, "2");
        SquashOntoEpic(repo, "1");
        SquashOntoEpic(repo, "2");
        var thrown = false;
        var pruner = new Pruner(m)
        {
            BeforeApply = e =>
            {
                if (!thrown)
                {
                    thrown = true;
                    throw new IOException("disk went away\nsecond line");
                }
            },
        };

        var report = pruner.Prune(null, false, false);

        Assert.Equal((1, 1), (report.Removed, report.Failed));
        var failed = report.Items.Single(i => i.Error is not null);
        Assert.Equal((false, false), (failed.Done, failed.BranchDeleted));
        Assert.Equal("disk went away second line", failed.Error);
        Assert.True(Directory.Exists(failed.Path));
        Assert.NotEmpty(repo.Git("branch", "--list", report.Items.Single(i => i.Error is not null).Branch));
        var done = report.Items.Single(i => i.Error is null);
        Assert.Equal((true, true), (done.Done, done.BranchDeleted));
        Assert.False(Directory.Exists(done.Path));
        Assert.Contains(new[] { first.Path, second.Path }, p => WorktreeList.SamePath(p, done.Path));
    }

    [Fact]
    public void MergedViaContent_RemovedAndBranchDeleted()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var done = Work(m, "1");
        var open = Work(m, "2");
        SquashOntoEpic(repo, "1");
        var report = new Pruner(m).Prune(null, dryRun: false, force: false);
        Assert.Equal((1, 1, 0), (report.Removed, report.Kept, report.Failed));
        Assert.False(Directory.Exists(done.Path));
        Assert.Empty(repo.Git("branch", "--list", done.Branch));
        Assert.False(BranchMetaStore.ReadAll(m.Git).ContainsKey(done.Branch));
        Assert.True(Directory.Exists(open.Path));
        Assert.Equal("2", Assert.Single(m.List()).Ticket);
    }

    [Fact]
    public void DryRun_ChangesNothing()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var done = Work(m, "1");
        SquashOntoEpic(repo, "1");
        var report = new Pruner(m).Prune(null, dryRun: true, force: false);
        var item = Assert.Single(report.Items);
        Assert.Equal((PruneActions.Remove, false), (item.Action, item.Done));
        Assert.True(Directory.Exists(done.Path));
        Assert.NotEmpty(repo.Git("branch", "--list", done.Branch));
    }

    [Fact]
    public void DryRunWithForce_LeavesGitStateExactlyAsItWas()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var merged = Work(m, "1");
        var dirty = Work(m, "2");
        var missing = Work(m, "3");
        SquashOntoEpic(repo, "1");
        File.WriteAllText(Path.Combine(dirty.Path, "scratch.txt"), "x");
        FileTree.DeleteTree(missing.Path);
        var before = Snapshot(repo, merged.Path, dirty.Path, missing.Path);

        var report = new Pruner(m).Prune(null, dryRun: true, force: true);

        Assert.Equal(before, Snapshot(repo, merged.Path, dirty.Path, missing.Path));
        Assert.Equal(3, report.Items.Count);
        Assert.All(report.Items, i => Assert.Equal((false, false, null), (i.Done, i.BranchDeleted, i.Error)));
        Assert.Equal((0, 0, 0), (report.Removed, report.Kept, report.Failed));
        Assert.True(report.DryRun && report.Force);
    }

    [Fact]
    public void Force_RemovesDirtyUnmergedWork()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        File.WriteAllText(Path.Combine(r.Path, "scratch.txt"), "x");
        Assert.Equal(1, new Pruner(m).Prune(null, false, false).Kept);
        var forced = new Pruner(m).Prune(null, false, true);
        Assert.Equal(1, forced.Removed);
        Assert.False(Directory.Exists(r.Path));
        Assert.Empty(repo.Git("branch", "--list", r.Branch));
    }

    [Fact]
    public void WithoutForce_DirtyAndUnmergedWorkIsLeftExactlyAsItWas()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var dirtyMerged = Work(m, "1");
        var unmerged = Work(m, "2");
        SquashOntoEpic(repo, "1");
        File.WriteAllText(Path.Combine(dirtyMerged.Path, "scratch.txt"), "x");
        var before = Snapshot(repo, dirtyMerged.Path, unmerged.Path);

        var report = new Pruner(m).Prune(null, false, false);

        Assert.Equal((0, 2, 0), (report.Removed, report.Kept, report.Failed));
        Assert.Equal(before, Snapshot(repo, dirtyMerged.Path, unmerged.Path));
    }

    [Fact]
    public void LockedWorktree_SurvivesForce()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        SquashOntoEpic(repo, "1");
        repo.Git("worktree", "lock", "--reason", "agent busy", r.Path);
        var report = new Pruner(m).Prune(null, false, true);
        Assert.Equal(PruneActions.Keep, Assert.Single(report.Items).Action);
        Assert.True(Directory.Exists(r.Path));
    }

    [Fact]
    public void MissingDirectory_MetadataPruned_UnmergedBranchKept()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        FileTree.DeleteTree(r.Path);
        var item = Assert.Single(new Pruner(m).Prune(null, false, false).Items);
        Assert.Equal((PruneActions.PruneMetadata, true, false), (item.Action, item.Done, item.BranchDeleted));
        Assert.Empty(m.List());
        Assert.NotEmpty(repo.Git("branch", "--list", r.Branch));
    }

    [Fact]
    public void MissingDirectory_MergedBranchDeletedWithMetadata_OtherStaleRegistrationsUntouched()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        SquashOntoEpic(repo, "1");
        FileTree.DeleteTree(r.Path);

        // An unmanaged worktree whose directory is also gone: its registration is not ours to drop.
        var mine = Path.Combine(repo.Sandbox, "mine");
        repo.Git("worktree", "add", "-q", "-b", "mine", mine);
        FileTree.DeleteTree(mine);

        var item = Assert.Single(new Pruner(m).Prune(null, false, false).Items);
        Assert.Equal((PruneActions.PruneMetadata, true, true, null), (item.Action, item.Done, item.BranchDeleted, item.Error));
        Assert.Empty(repo.Git("branch", "--list", r.Branch));
        Assert.Empty(BranchMetaStore.ReadAll(m.Git));
        Assert.Contains(WorktreeList.Read(m.Git), w => WorktreeList.SamePath(w.Path, mine));
        Assert.DoesNotContain(WorktreeList.Read(m.Git), w => WorktreeList.SamePath(w.Path, r.Path));
    }

    [Fact]
    public void BaseFilter_LeavesOtherEpicsAndUnmanagedWorktreesAlone()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        repo.Epic(name: "epic/7-other");
        var other = m.Create(new CreateRequest("2", "y", "epic/7-other", null));
        var mine = Path.Combine(repo.Sandbox, "mine");
        repo.Git("worktree", "add", "-q", "-b", "mine", mine);
        var report = new Pruner(m).Prune(Epic, false, true);
        Assert.Empty(report.Items);
        Assert.True(Directory.Exists(other.Path) && Directory.Exists(mine));
    }

    [Fact]
    public void MainCheckoutOnATaskBranch_IsNeverTouched()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        SquashOntoEpic(repo, "1");
        repo.Git("worktree", "remove", r.Path);
        repo.Git("checkout", "-q", r.Branch);

        var item = Assert.Single(new Pruner(m).Prune(null, false, true).Items);

        Assert.Equal((PruneActions.Keep, false), (item.Action, item.Done));
        Assert.Contains("main checkout", item.Reason, StringComparison.Ordinal);
        Assert.Equal(r.Branch, repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.True(BranchMetaStore.ReadAll(m.Git).ContainsKey(r.Branch));
    }

    [Fact]
    public void UnassessableWorktree_KeptEvenWithForce_OthersStillPruned()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var broken = Work(m, "1");
        var fine = Work(m, "2");
        SquashOntoEpic(repo, "1");
        SquashOntoEpic(repo, "2");
        WorktreeManagerTests.CorruptGitFile(broken.Path);

        var report = new Pruner(m).Prune(null, false, true);

        Assert.Equal((1, 1, 0), (report.Removed, report.Kept, report.Failed));
        var kept = report.Items.Single(i => i.Action == PruneActions.Keep);
        Assert.Equal(broken.Branch, kept.Branch);
        Assert.StartsWith("could not assess: ", kept.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(broken.Path));
        Assert.NotEmpty(repo.Git("branch", "--list", broken.Branch));
        Assert.False(Directory.Exists(fine.Path));
        Assert.Empty(repo.Git("branch", "--list", fine.Branch));
    }

    [Fact]
    public void CommitMadeAfterTheMergeCheck_KeepsTheBranch()
    {
        var (repo, m) = Setup();
        using var _ = repo;
        var r = Work(m, "1");
        SquashOntoEpic(repo, "1");
        string? late = null;

        // Another agent commits new work between listing (which found the branch merged) and removal.
        var pruner = new Pruner(m)
        {
            BeforeApply = e =>
            {
                File.WriteAllText(Path.Combine(e.Path, "late.txt"), "late\n");
                TempRepo.RunGit(e.Path, "add", "-A");
                TempRepo.RunGit(e.Path, "commit", "-q", "-m", "late work");
                late = TempRepo.RunGit(e.Path, "rev-parse", "HEAD");
            },
        };
        var item = Assert.Single(pruner.Prune(null, false, false).Items);

        Assert.Equal((true, false), (item.Done, item.BranchDeleted));
        Assert.StartsWith("branch kept: ", item.Error, StringComparison.Ordinal);
        Assert.Equal(late, repo.Sha(r.Branch));
        Assert.True(BranchMetaStore.ReadAll(m.Git).ContainsKey(r.Branch));
    }

    [Fact]
    public void HeldFile_FailsItemKeepsBranchContinues()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (repo, m) = Setup();
        using var _ = repo;
        var held = Work(m, "1");
        var free = Work(m, "2");
        SquashOntoEpic(repo, "1");
        SquashOntoEpic(repo, "2");

        // Make f1.txt stat-clean (older than the index): otherwise git's racy-clean check makes `status` read it, the held handle
        // fails that read, and List reports the worktree as unassessable (kept) instead of reaching the remove under test.
        var heldFile = Path.Combine(held.Path, "f1.txt");
        File.SetLastWriteTimeUtc(heldFile, DateTime.UtcNow.AddHours(-1));
        TempRepo.RunGit(held.Path, "update-index", "--refresh");
        PruneReport report;
        using (new FileStream(Path.Combine(held.Path, "f1.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = new Pruner(m).Prune(null, false, false);
        }

        Assert.Equal((1, 1), (report.Removed, report.Failed));
        var failed = report.Items.Single(i => i.Error is not null);
        Assert.Equal(held.Branch, failed.Branch);
        Assert.False(failed.Done);
        Assert.False(failed.BranchDeleted);
        Assert.Contains($"'{held.Path}' left behind", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", failed.Error, StringComparison.Ordinal);
        Assert.NotEmpty(repo.Git("branch", "--list", held.Branch));
        Assert.False(Directory.Exists(free.Path));

        // C6, observed with git 2.54: the failed remove already dropped the registration; the directory stays behind.
        Assert.True(Directory.Exists(held.Path));
        Assert.Contains("no longer registered", failed.Error, StringComparison.Ordinal);
        Assert.Empty(m.List());
    }
}
