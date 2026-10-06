using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicCloserTests
{
    const string Epic = "epic/42-auth";

    sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Repo = TempRepo.Create();
            Config = TestConfig.For(Repo);
            Paths = RepoLocator.Locate(Repo.Root);
            new EpicOpener(Paths, Config).Open("42", "auth", null, null);
            // Plan B-shaped commits: "{ticket}: {title}" subjects and the batch epic id in Epic:.
            OnEpic("9933: Login form\n\nTicket: 9933\nEpic: 42-auth\nBatch: 1\nSwarm-Run: run-1", ("t1.txt", "1\n"));
            OnEpic("9934: Token refresh\n\nTicket: 9934\nEpic: 42-auth\nBatch: 2\nSwarm-Run: run-1", ("t2.txt", "2\n"));
            MainBefore = Repo.Sha("main");
        }

        public TempRepo Repo { get; }

        public SwarmConfig Config { get; }

        public RepoPaths Paths { get; }

        public string MainBefore { get; }

        public EpicStore Store => new(new StateLayout(Repo.StateDir));

        public string CloseWorktree => Path.Combine(Repo.WorktreeRoot, "close-42");

        public EpicCloser Closer() => new(Paths, Config);

        public EpicCloseResult Close(CloseOptions? options = null) => Closer().Close("42", options ?? new CloseOptions());

        public void OnEpic(string message, params (string Path, string Content)[] files)
        {
            var back = Repo.Git("rev-parse", "--abbrev-ref", "HEAD");
            Repo.Git("checkout", "-q", Epic);
            Repo.Commit(message, files);
            Repo.Git("checkout", "-q", back);
        }

        public void ReturnedTask() =>
            RunStateFixture.WriteRun(Repo.StateDir, "r1", DateTime.UtcNow, Epic, [], [RunStateFixture.Returned("T3", "task/T3", FinalState.ReturnedRed)]);

        // The per-epic lock batch run and squash run take (slot 0 of locks/batch-42-auth).
        public SlotLease? TryLock() =>
            new SlotSemaphore(new StateLayout(Repo.StateDir).BatchLockDir("42-auth"), SlotOptions.From(Config) with { Slots = 1, MaxWait = null }).TryAcquire("test");

        public bool LockFree()
        {
            using var lease = TryLock();
            return lease is not null;
        }

        public void Dispose() => Repo.Dispose();
    }

    [Fact]
    public void ActiveCheckedOut_FastForwardsWorkingTree()
    {
        using var f = new Fixture();
        var r = f.Close();
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(f.Repo.Sha("main"), r.MergeCommit);
        var parents = f.Repo.Git("rev-list", "--parents", "-n", "1", "main").Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal(f.MainBefore, parents[1]);
        Assert.Equal(f.Repo.Sha(Epic), parents[2]);
        Assert.True(File.Exists(Path.Combine(f.Repo.Root, "t2.txt")));
        Assert.StartsWith("Merge epic 42-auth (epic/42-auth) into main", f.Repo.Git("log", "-1", "--format=%B", "main"));
        Assert.Contains("Tickets: 9933, 9934", f.Repo.Git("log", "-1", "--format=%B", "main"));
        Assert.Equal(new[] { "9933", "9934" }, r.Tickets);
        Assert.Contains("- 9933 (batch 1): Login form", r.Message);
        Assert.DoesNotContain("another epic", r.Message);
        Assert.Equal(2, f.Repo.Git("log", "--first-parent", "--format=%H", "main").Split('\n').Length);
        Assert.Equal((EpicStates.Closed, "main", r.MergeCommit), (f.Store.Get("42").State, f.Store.Get("42").MergedInto, f.Store.Get("42").MergeCommit));
        Assert.False(Directory.Exists(f.CloseWorktree));
        Assert.Empty(r.Warnings);
        Assert.Empty(f.Repo.Git("status", "--porcelain", "--untracked-files=no"));
    }

    [Fact]
    public void ActiveNotCheckedOut_UpdatesRefOnly()
    {
        using var f = new Fixture();
        f.Repo.Git("checkout", "-q", "-b", "other", f.MainBefore);
        var r = f.Close();
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(r.MergeCommit, f.Repo.Sha("main"));
        Assert.Equal("other", f.Repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.False(File.Exists(Path.Combine(f.Repo.Root, "t2.txt")));
    }

    [Fact]
    public void StaleRefLock_ReportsGitsReasonNotMoved()
    {
        using var f = new Fixture();
        f.Repo.Git("checkout", "-q", "-b", "other", f.MainBefore);
        var lockFile = f.Repo.LockRef("main");
        var e = Assert.Throws<ToolException>(() => f.Close());
        File.Delete(lockFile);
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not move 'main'", e.Message);
        Assert.DoesNotContain("moved during close", e.Message);
        Assert.DoesNotContain('\n', e.Message);
        Assert.Contains(".lock", e.Hint);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.True(f.LockFree());
        Assert.False(Directory.Exists(f.CloseWorktree));
    }

    [Fact]
    public void Blocked_ChangesNothing()
    {
        using var f = new Fixture();
        f.ReturnedTask();
        var r = f.Close();
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Equal(BlockerCodes.TasksReturned, Assert.Single(r.Blockers).Code);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void Force_WaivesTasksReturned()
    {
        using var f = new Fixture();
        f.ReturnedTask();
        var r = f.Close(new CloseOptions(Force: true));
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(BlockerCodes.TasksReturned, Assert.Single(r.Waived).Code);
        Assert.Empty(r.Blockers);
    }

    [Fact]
    public void Force_DoesNotWaiveActiveDirty()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Repo.Root, "README.md"), "edited\n");
        var r = f.Close(new CloseOptions(Force: true));
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Contains(r.Blockers, b => b.Code == BlockerCodes.ActiveDirty);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
    }

    [Fact]
    public void Force_DoesNotWaiveNothingToMerge()
    {
        using var f = new Fixture();
        f.Repo.Git("update-ref", "refs/heads/" + Epic, f.MainBefore);
        var r = f.Close(new CloseOptions(Force: true));
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Equal(BlockerCodes.NothingToMerge, Assert.Single(r.Blockers).Code);
        Assert.Empty(r.Waived);
    }

    [Fact]
    public void BatchRunning_BlocksEvenWithForce()
    {
        using var f = new Fixture();
        using var held = f.TryLock();
        Assert.NotNull(held);
        var r = f.Close(new CloseOptions(Force: true));
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Equal(BlockerCodes.BatchRunning, Assert.Single(r.Blockers).Code);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
    }

    [Fact]
    public void LockTakenAfterAssessment_FailsWithEnvironmentAndChangesNothing()
    {
        using var f = new Fixture();
        SlotLease? held = null;
        var closer = f.Closer();
        closer.BeforeLock = () => held = f.TryLock();
        try
        {
            var e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));
            Assert.Equal(ExitCodes.Environment, e.ExitCode);
            Assert.Contains("a batch or squash run holds epic '42-auth'", e.Message);
            Assert.DoesNotContain('\n', e.Message);
        }
        finally
        {
            held?.Dispose();
        }

        Assert.NotNull(held);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void BlockerAppearingBeforeLock_IsCaughtByReassessment()
    {
        using var f = new Fixture();
        var closer = f.Closer();
        closer.BeforeLock = f.ReturnedTask;
        var r = closer.Close("42", new CloseOptions());
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Equal(BlockerCodes.TasksReturned, Assert.Single(r.Blockers).Code);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.True(f.LockFree());
    }

    [Fact]
    public void EpicLock_HeldDuringMergeAndReleasedAfter()
    {
        using var f = new Fixture();
        bool? freeDuringMerge = null;
        var closer = f.Closer();
        closer.AfterMerge = _ => freeDuringMerge = f.LockFree();
        Assert.Equal(CloseResults.Merged, closer.Close("42", new CloseOptions()).Result);
        Assert.False(freeDuringMerge);
        Assert.True(f.LockFree());
    }

    [Fact]
    public void TargetMovedDuringClose_NotCheckedOut_ReportsMoved()
    {
        using var f = new Fixture();
        f.Repo.Git("checkout", "-q", "-b", "other", f.MainBefore);
        string? moved = null;
        var closer = f.Closer();
        closer.AfterMerge = _ =>
        {
            moved = f.Repo.Git("commit-tree", "-p", f.MainBefore, "-m", "concurrent", f.MainBefore + "^{tree}");
            f.Repo.Git("update-ref", "refs/heads/main", moved);
        };
        var e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("'main' moved during close", e.Message);
        Assert.Equal(moved, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.True(f.LockFree());
    }

    [Fact]
    public void TargetMovedDuringClose_CheckedOut_ReportsMoved()
    {
        using var f = new Fixture();
        string? moved = null;
        var closer = f.Closer();
        closer.AfterMerge = _ => moved = f.Repo.Commit("concurrent", ("c.txt", "c\n"));
        var e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("'main' moved during close", e.Message);
        Assert.Equal(moved, f.Repo.Sha("main"));
        Assert.False(File.Exists(Path.Combine(f.Repo.Root, "t2.txt")));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void UntrackedFileInTheWay_FailsFastForwardAndChangesNothing()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Repo.Root, "t2.txt"), "mine\n");
        var e = Assert.Throws<ToolException>(() => f.Close());
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not fast-forward 'main'", e.Message);
        Assert.DoesNotContain('\n', e.Message);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(f.Repo.Root, "t2.txt")));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.True(f.LockFree());
    }

    [Fact]
    public void CleanCrlfCheckoutUnderRepoAutoCrlf_FastForwardsAndWritesTheUsersLineEndings()
    {
        using var f = new Fixture();
        f.OnEpic("9935: readme\n\nTicket: 9935", ("README.md", "readme\nmore\n"));
        LineEndings.UseAutoCrlf(f.Repo);
        var readme = Path.Combine(f.Repo.Root, "README.md");
        LineEndings.CleanCrlfCheckout(f.Repo.Root, "README.md");
        Assert.True(LineEndings.ModifiedUnderAutoCrlfOff(f.Repo.Root, "README.md"));
        LineEndings.Touch(readme);

        // Touched again before the fast-forward: the assessment's own status refreshed the index, and the fast-forward
        // re-checks the file it is about to replace.
        var closer = f.Closer();
        closer.AfterMerge = _ => LineEndings.Touch(readme);
        var r = closer.Close("42", new CloseOptions());

        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(r.MergeCommit, f.Repo.Sha("main"));
        Assert.Equal("readme\r\nmore\r\n", File.ReadAllText(readme));
        Assert.Equal(EpicStates.Closed, f.Store.Get("42").State);
    }

    // main gets a.txt, b.txt and c.txt; the epic (with main merged in) changes all three. Returns main's tip.
    static string EpicEditsAbc(Fixture f)
    {
        f.Repo.Commit("abc", ("a.txt", "1\n"), ("b.txt", "1\n"), ("c.txt", "1\n"));
        f.Repo.Git("checkout", "-q", Epic);
        f.Repo.Git("merge", "-q", "--no-edit", "main");
        f.Repo.Commit("9935: edit abc\n\nTicket: 9935", ("a.txt", "2\n"), ("b.txt", "2\n"), ("c.txt", "2\n"));
        f.Repo.Git("checkout", "-q", "main");
        return f.Repo.Sha("main");
    }

    [Fact]
    public void UserEditMadeAfterTheAssessment_StopsTheFastForwardAndIsNeverOfferedForCheckout()
    {
        using var f = new Fixture();
        var mainBefore = EpicEditsAbc(f);
        var a = Path.Combine(f.Repo.Root, "a.txt");
        var closer = f.Closer();
        closer.AfterMerge = _ => File.WriteAllText(a, "mine\n");

        var e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));

        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not fast-forward 'main'", e.Message);
        Assert.DoesNotContain("checkout --", e.Message);
        Assert.Equal("mine\n", File.ReadAllText(a));
        Assert.Equal(mainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void HeldFileStopsTheFastForwardHalfWay_ReportsHowToRestoreTheTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var f = new Fixture();
        var mainBefore = EpicEditsAbc(f);

        // b.txt stat-clean (older than the index), so git trusts it without reading and fails only when it replaces it.
        var held = Path.Combine(f.Repo.Root, "b.txt");
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddHours(-1));
        f.Repo.Git("update-index", "--refresh");
        FileStream? handle = null;
        var closer = f.Closer();
        closer.AfterMerge = _ => handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        ToolException e;
        try
        {
            e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));
        }
        finally
        {
            handle?.Dispose();
        }

        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not fast-forward 'main'", e.Message);
        Assert.DoesNotContain('\n', e.Message);
        Assert.Matches(@"already rewrote \d+ tracked file\(s\) in '.+' before it stopped; restore them with: git -C \S+ checkout -- a\.txt (b\.txt )?c\.txt", e.Message);
        Assert.Contains("delete the file(s) it created: t1.txt t2.txt", e.Message);
        Assert.Equal(mainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.Equal("2\n", File.ReadAllText(Path.Combine(f.Repo.Root, "a.txt")));

        // Following the advice restores the checkout, and the close then succeeds.
        f.Repo.Git("checkout", "--", "a.txt", "b.txt", "c.txt");
        File.Delete(Path.Combine(f.Repo.Root, "t1.txt"));
        File.Delete(Path.Combine(f.Repo.Root, "t2.txt"));
        Assert.Empty(f.Repo.Git("status", "--porcelain"));
        Assert.Equal(CloseResults.Merged, f.Close().Result);
    }

    [Fact]
    public void TargetNotAtTheMergeCommitAfterTheMove_IsNotRecordedClosed()
    {
        using var f = new Fixture();
        f.Repo.Git("checkout", "-q", "-b", "other", f.MainBefore);
        string? moved = null;
        var closer = f.Closer();
        closer.AfterMove = merge =>
        {
            moved = f.Repo.Git("commit-tree", "-p", merge, "-m", "concurrent", merge + "^{tree}");
            f.Repo.Git("update-ref", GitRunner.HeadsRef("main"), moved);
        };

        var e = Assert.Throws<ToolException>(() => closer.Close("42", new CloseOptions()));

        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("does not point at the merge commit", e.Message);
        Assert.Contains("the epic was not recorded as closed", e.Message);
        Assert.Equal(moved, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.True(f.LockFree());
    }

    [Fact]
    public void MergeMessageLongerThanTheWindowsCommandLine_IsPassedByFile()
    {
        using var f = new Fixture();
        var message = Path.Combine(f.Repo.Sandbox, "msg.txt");
        File.WriteAllText(message, $"9936: {new string('x', 40_000)}\n\nTicket: 9936\n");
        f.Repo.Git("checkout", "-q", Epic);
        f.Repo.Write("big.txt", "big\n");
        f.Repo.Git("add", "-A");
        f.Repo.Git("commit", "-q", "-F", message);
        f.Repo.Git("checkout", "-q", "main");

        var r = f.Close();

        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.True(r.Message.Length > 32_768);
        Assert.Equal(r.Message, f.Repo.Git("log", "-1", "--format=%B", "main").TrimEnd('\n'));
    }

    [Fact]
    public void ActiveBranchBeingRebased_RefusesAndChangesNothing()
    {
        // The reviewer's scenario: main stopped at an `edit` step (clean tree, worktree detached). Moving main now would be
        // undone by `git rebase --abort`, leaving a record that says closed.
        using var f = new Fixture();
        var m1 = f.Repo.Commit("m1", ("m.txt", "m\n"));
        var git = new GitRunner(f.Repo.Root);
        var stop = git.Try("-c", "sequence.editor=sed -i -e s/^pick/edit/", "rebase", "-i", "HEAD~1");
        Assert.True(stop.ExitCode == 0, stop.StdErr);
        Assert.Equal("HEAD", f.Repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        try
        {
            var e = Assert.Throws<ToolException>(() => f.Close());
            Assert.Equal(ExitCodes.Environment, e.ExitCode);
            Assert.Contains("'main' is being rebased in", e.Message);
            Assert.Contains("rebase --continue or --abort", e.Hint);
            Assert.DoesNotContain('\n', e.Message);
            Assert.Equal(m1, f.Repo.Sha("main"));
            Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
            Assert.True(f.LockFree());
        }
        finally
        {
            git.Try("rebase", "--abort");
        }

        Assert.Equal(m1, f.Repo.Sha("main"));
    }

    [Fact]
    public void IgnoredLocalFileTheEpicAdds_IsNotOverwritten()
    {
        using var f = new Fixture();
        f.OnEpic("9935: add secret\n\nTicket: 9935", ("secret.txt", "from epic\n"));
        var mainNow = f.Repo.Commit("ignore secret", (".gitignore", "secret.txt\n"));
        var secret = Path.Combine(f.Repo.Root, "secret.txt");
        File.WriteAllText(secret, "mine\n");
        var e = Assert.Throws<ToolException>(() => f.Close());
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("could not fast-forward 'main'", e.Message);
        Assert.Equal("mine\n", File.ReadAllText(secret));
        Assert.Equal(mainNow, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void TargetComesToContainTheEpicBeforeTheLock_IsBlockedNotRecorded()
    {
        using var f = new Fixture();
        f.Repo.Git("checkout", "-q", "-b", "other", f.MainBefore);
        var closer = f.Closer();
        closer.BeforeLock = () => f.Repo.Git("update-ref", "refs/heads/main", f.Repo.Sha(Epic));
        var r = closer.Close("42", new CloseOptions(Force: true));
        Assert.Equal(CloseResults.Blocked, r.Result);
        Assert.Equal(BlockerCodes.NothingToMerge, Assert.Single(r.Blockers).Code);
        Assert.Equal(f.Repo.Sha(Epic), f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
    }

    [Fact]
    public void RejectingHooks_DoNotRunOnTheToolMadeMerge()
    {
        using var f = new Fixture();
        var hooks = Path.GetFullPath(Path.Combine(f.Repo.Root, f.Repo.Git("rev-parse", "--git-path", "hooks")));
        Directory.CreateDirectory(hooks);
        foreach (var hook in (string[])["commit-msg", "pre-merge-commit"])
        {
            File.WriteAllText(Path.Combine(hooks, hook), "#!/bin/sh\necho rejected by policy >&2\nexit 1\n");
        }

        var r = f.Close();
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(r.MergeCommit, f.Repo.Sha("main"));
        Assert.Equal(3, f.Repo.Git("rev-list", "--parents", "-n", "1", "main").Split(' ').Length);
    }

    [Fact]
    public void MergeLogConfig_DoesNotChangeTheToolsMessage()
    {
        using var f = new Fixture();
        f.Repo.Git("config", "merge.log", "true");
        var r = f.Close();
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(r.Message, f.Repo.Git("log", "-1", "--format=%B", "main").TrimEnd('\n'));
    }

    [Fact]
    public void Conflict_ReportsFilesAndLeavesMainAlone()
    {
        using var f = new Fixture();
        f.OnEpic("epic side", ("shared.txt", "epic\n"));
        f.Repo.Commit("main side", ("shared.txt", "main\n"));
        var mainNow = f.Repo.Sha("main");
        var r = f.Close();
        Assert.Equal(CloseResults.Conflict, r.Result);
        Assert.Equal(new[] { "shared.txt" }, r.ConflictFiles);
        Assert.Equal(mainNow, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
        Assert.False(Directory.Exists(f.CloseWorktree));
        Assert.DoesNotContain(WorktreeList.Read(new GitRunner(f.Repo.Root)), w => w.Path.EndsWith("close-42", StringComparison.OrdinalIgnoreCase));
        Assert.True(f.LockFree());
    }

    [Fact]
    public void DryRun_ChangesNothingAndReturnsMessage()
    {
        using var f = new Fixture();
        var r = f.Close(new CloseOptions(DryRun: true));
        Assert.Equal(CloseResults.DryRun, r.Result);
        Assert.Contains("- 9934 (batch 2): Token refresh", r.Message);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.False(Directory.Exists(f.CloseWorktree));
    }

    [Fact]
    public void AlreadyClosed_IsBadInput()
    {
        using var f = new Fixture();
        f.Close();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => f.Close()).ExitCode);
    }

    [Fact]
    public void UnknownEpic_IsBadInput()
    {
        using var f = new Fixture();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => f.Closer().Close("43", new CloseOptions())).ExitCode);
    }

    [Fact]
    public void MissingTarget_IsBadInput()
    {
        using var f = new Fixture();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => f.Close(new CloseOptions(Into: "nope"))).ExitCode);
    }

    [Fact]
    public void DeleteBranch_RemovesMergedEpicBranch()
    {
        using var f = new Fixture();
        Assert.True(f.Close(new CloseOptions(DeleteBranch: true)).BranchDeleted);
        Assert.Empty(f.Repo.Git("branch", "--list", Epic));
    }

    [Fact]
    public void DeleteBranch_KeepsEpicThatMovedDuringClose()
    {
        using var f = new Fixture();
        var closer = f.Closer();
        closer.AfterMerge = _ => f.OnEpic("late", ("late.txt", "late\n"));
        var r = closer.Close("42", new CloseOptions(DeleteBranch: true));
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.False(r.BranchDeleted);
        Assert.True(f.Repo.HasFile(Epic, "late.txt"));
        Assert.False(f.Repo.HasFile("main", "late.txt"));
        Assert.Contains(r.Warnings, w => w.Contains("moved during close", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteBranch_KeepsEpicCheckedOutInAWorktree()
    {
        using var f = new Fixture();
        var elsewhere = Path.Combine(f.Repo.Sandbox, "epic-wt");
        f.Repo.Git("worktree", "add", "-q", elsewhere, Epic);
        var r = f.Close(new CloseOptions(DeleteBranch: true));
        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.False(r.BranchDeleted);
        Assert.Equal(r.MergeCommit, f.Repo.Sha("main"));
        Assert.Contains(r.Warnings, w => w.Contains("checked out", StringComparison.Ordinal));
        Assert.Equal(Epic, TempRepo.RunGit(elsewhere, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public void DeleteBranch_KeepsEpicBeingRebasedInAWorktree()
    {
        using var f = new Fixture();
        var elsewhere = Path.Combine(f.Repo.Sandbox, "epic-wt");
        f.Repo.Git("worktree", "add", "-q", elsewhere, Epic);
        var git = new GitRunner(elsewhere);

        // Stopped at an `edit` step: the worktree is detached, so the epic branch reads as checked out nowhere.
        var stop = git.Try("-c", "sequence.editor=sed -i -e s/^pick/edit/", "rebase", "-i", "HEAD~1");
        Assert.True(stop.ExitCode == 0, stop.StdErr);
        try
        {
            var r = f.Close(new CloseOptions(DeleteBranch: true));
            Assert.Equal(CloseResults.Merged, r.Result);
            Assert.False(r.BranchDeleted);
            Assert.Contains(r.Warnings, w => w.Contains("being rebased in", StringComparison.Ordinal));
            Assert.NotEmpty(f.Repo.Git("branch", "--list", Epic));
        }
        finally
        {
            git.Try("rebase", "--abort");
        }
    }

    [Fact]
    public void StaleCloseWorktree_IsReplaced()
    {
        using var f = new Fixture();
        f.Repo.Git("worktree", "add", "-q", "--detach", f.CloseWorktree, "main");
        File.WriteAllText(Path.Combine(f.CloseWorktree, "junk.txt"), "x");
        Assert.Equal(CloseResults.Merged, f.Close().Result);
        Assert.False(Directory.Exists(f.CloseWorktree));
    }

    [Fact]
    public void HeldFileInCloseWorktree_ReportedNotClaimedRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var f = new Fixture();
        FileStream? held = null;
        var closer = f.Closer();
        closer.AfterMerge = _ => held = new FileStream(Path.Combine(f.CloseWorktree, "t1.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        EpicCloseResult r;
        try
        {
            r = closer.Close("42", new CloseOptions());
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(CloseResults.Merged, r.Result);
        Assert.Equal(r.MergeCommit, f.Repo.Sha("main"));
        Assert.True(Directory.Exists(f.CloseWorktree));
        var w = Assert.Single(r.Warnings);
        Assert.Contains("left behind", w);
        Assert.Contains(f.CloseWorktree, w);

        // The next close of another epic (or a retry) replaces the leftover directory.
        FileTree.DeleteTree(f.CloseWorktree);
    }
}
