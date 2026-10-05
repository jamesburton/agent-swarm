using System.Diagnostics;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicAssessorTests
{
    const string Epic = "epic/42-auth";
    static readonly DateTime T0 = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

    sealed class Fixture : IDisposable
    {
        public Fixture(bool withCommit = true)
        {
            Repo = TempRepo.Create();
            Config = TestConfig.For(Repo);
            Paths = RepoLocator.Locate(Repo.Root);
            new EpicOpener(Paths, Config).Open("42", "auth", null, null);
            if (withCommit)
            {
                // Plan B stamps the batch epic id (42-auth for epic/42-auth with epicBranchTemplate epic/{epic}).
                OnEpic("9933: work\n\nTicket: 9933\nEpic: 42-auth\nBatch: 1\nSwarm-Run: r0", ("t1.txt", "1\n"));
            }
        }

        public TempRepo Repo { get; }

        public SwarmConfig Config { get; }

        public RepoPaths Paths { get; }

        public EpicStatus Assess(SwarmConfig? config = null) =>
            new EpicAssessor(Paths, config ?? Config).Assess(new EpicStore(new StateLayout(Repo.StateDir)).Get("42"));

        public void OnEpic(string message, params (string Path, string Content)[] files)
        {
            Repo.Git("checkout", "-q", Epic);
            Repo.Commit(message, files);
            Repo.Git("checkout", "-q", "main");
        }

        public void Dispose() => Repo.Dispose();
    }

    static IEnumerable<string> Codes(EpicStatus s) => s.Blockers.Select(b => b.Code);

    [Fact]
    public void ReadyEpic_HasNoBlockers()
    {
        using var f = new Fixture();
        var s = f.Assess();
        Assert.True(s.ReadyToClose);
        Assert.Equal((1, 0, "42-auth", "main"), (s.Ahead, s.Behind, s.BatchEpic, s.Into));
        Assert.Equal(f.Repo.Sha(Epic), s.Tip);
    }

    [Fact]
    public void NoCommits_NothingToMerge()
    {
        using var f = new Fixture(withCommit: false);
        var b = Assert.Single(f.Assess().Blockers);
        Assert.Equal((BlockerCodes.NothingToMerge, false), (b.Code, b.Waivable));
    }

    [Fact]
    public void ReturnedTask_BlocksUntilALaterRunLandsIt()
    {
        using var f = new Fixture();
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, Epic, [new LandedRecord("T1", 1, "c", "task/T1")], [RunStateFixture.Returned("T3", "task/T3", FinalState.ReturnedRed)]);
        var s = f.Assess();
        var b = Assert.Single(s.Blockers);
        Assert.Equal((BlockerCodes.TasksReturned, true), (b.Code, b.Waivable));
        Assert.Contains("T3 (returned-red)", b.Detail);
        Assert.Equal(("T3", 1), (Assert.Single(s.OpenTasks).Task, s.LatestRunExitCode));
        Assert.Equal(new[] { "T1" }, s.LandedTasks);

        RunStateFixture.WriteRun(f.Repo.StateDir, "r2", T0.AddHours(1), Epic, [new LandedRecord("T3", 1, "c", "task/T3")], []);
        Assert.True(f.Assess().ReadyToClose);
    }

    [Fact]
    public void ReturnedTask_LandedLaterBySquashRun_DoesNotBlock()
    {
        using var f = new Fixture();
        f.Repo.Branch("task/T3", Epic, ("t3.txt", "3\n"));
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, Epic, [], [RunStateFixture.Returned("T3", "task/T3", FinalState.NeedsWorker, ReturnKind.Conflict)]);
        Assert.Equal(BlockerCodes.TasksReturned, Assert.Single(f.Assess().Blockers).Code);

        // A real squash run (Plan B): it writes no run state, so only the merged check can see this landing (C5).
        var context = new ToolContext(f.Paths, f.Config with { Epic = "42-auth" }, new StateLayout(f.Repo.StateDir), Verbosity.Quiet);
        var squashed = new SquashRunner(context, new Progress(TextWriter.Null, Verbosity.Quiet)).Run(new SquashRunRequest("T3", "task/T3", null, null));
        Assert.Equal((ExitCodes.Ok, false), (squashed.ExitCode, squashed.Empty));
        var s = f.Assess();
        Assert.Empty(s.OpenTasks);
        Assert.True(s.ReadyToClose);
    }

    [Fact]
    public void LandFailureWithoutFiles_ReportsGitOutputAsReason()
    {
        using var f = new Fixture();
        const string noTicket = "no ticket for task 'T5' (branch 'task/T5'): squash.ticketPattern 'x' matches neither and squash.requireTicket is true; nothing landed from this batch";
        var entry = RunStateFixture.Returned("T5", "task/T5", FinalState.NeedsWorker, ReturnKind.Conflict)
            with { Stage = ReturnStage.Land, Reason = "land conflict with the epic tip", GitOutput = noTicket };
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, Epic, [], [entry]);
        Assert.Equal(noTicket, Assert.Single(f.Assess().OpenTasks).Reason);
    }

    [Fact]
    public void ReturnWithFiles_KeepsItsReason()
    {
        using var f = new Fixture();
        var entry = RunStateFixture.Returned("T6", "task/T6", FinalState.NeedsWorker, ReturnKind.Conflict)
            with { Stage = ReturnStage.Land, Files = ["a.txt"], Reason = "land conflict with the epic tip", GitOutput = "CONFLICT (content): a.txt\nmore" };
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, Epic, [], [entry]);
        Assert.Equal("land conflict with the epic tip", Assert.Single(f.Assess().OpenTasks).Reason);
    }

    [Fact]
    public void UnprocessedTask_Blocks()
    {
        using var f = new Fixture();
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, Epic, [], [], unprocessed: ["T4"], exitCode: 5);
        Assert.Contains("T4 (unprocessed)", Assert.Single(f.Assess().Blockers).Detail);
    }

    [Fact]
    public void UnfinishedRun_BlocksWaivably()
    {
        using var f = new Fixture();
        RunStateFixture.WriteRun(f.Repo.StateDir, "crashed", T0, Epic, [], [], finished: false);
        var b = Assert.Single(f.Assess().Blockers);
        Assert.Equal((BlockerCodes.RunUnfinished, true), (b.Code, b.Waivable));
    }

    [Fact]
    public void RunsOfOtherEpics_AreIgnored()
    {
        using var f = new Fixture();
        RunStateFixture.WriteRun(f.Repo.StateDir, "r1", T0, "epic/7-other", [], [RunStateFixture.Returned("T9", "task/T9", FinalState.NeedsWorker)]);
        var s = f.Assess();
        Assert.True(s.ReadyToClose);
        Assert.Equal(0, s.Runs);
    }

    [Fact]
    public void EmptyOrPartlyPrunedRunFolder_DoesNotCrash()
    {
        using var f = new Fixture();

        // RunDirectories.Prune (or a crash before run-start) can leave a folder with no files: it has no epic and is ignored.
        Directory.CreateDirectory(new StateLayout(f.Repo.StateDir).RunDir("gutted"));
        var s = f.Assess();
        Assert.True(s.ReadyToClose);
        Assert.Equal(0, s.Runs);
    }

    [Fact]
    public void LiveBatchLock_BlocksAsRunning_NotAsUnfinished()
    {
        using var f = new Fixture();
        RunStateFixture.WriteRun(f.Repo.StateDir, "live", T0, Epic, [], [], finished: false);
        using var held = new SlotSemaphore(new StateLayout(f.Repo.StateDir).BatchLockDir("42-auth"), SlotOptions.From(f.Config) with { Slots = 1 }).Acquire("batch run live");
        var s = f.Assess();
        Assert.True(s.BatchRunning);
        var b = Assert.Single(s.Blockers);
        Assert.Equal((BlockerCodes.BatchRunning, false), (b.Code, b.Waivable));
        Assert.Contains("a batch or squash run holds epic '42-auth'", b.Detail);
    }

    [Theory]
    [InlineData("local-alive", true, true)] // suspended local holder: batch's acquire never takes it over
    [InlineData("local-alive", false, true)]
    [InlineData("local-dead", false, false)] // crashed local holder: reclaimable now
    [InlineData("remote", false, true)] // another host, heartbeat fresh
    [InlineData("remote", true, false)] // another host, heartbeat expired: reclaimable
    [InlineData("unreadable", false, true)] // mid-write: treated as held until it expires
    [InlineData("unreadable", true, false)]
    public void LockHolder_CountsAsRunningOnlyWhenBatchCouldNotTakeTheLock(string holder, bool stale, bool running)
    {
        using var f = new Fixture();
        var config = f.Config with { ExpirySec = 600 };
        RunStateFixture.WriteRun(f.Repo.StateDir, "crashed", T0, Epic, [], [], finished: false);
        var dir = new StateLayout(f.Repo.StateDir).BatchLockDir("42-auth");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "slot-0.lock");
        using (var me = Process.GetCurrentProcess())
        {
            var start = me.StartTime.ToUniversalTime();
            File.WriteAllText(file, holder switch
            {
                "local-alive" => SwarmJson.Line(new LockInfo(1, Environment.ProcessId, Environment.MachineName, "batch run x", T0, start)),
                "local-dead" => SwarmJson.Line(new LockInfo(1, Environment.ProcessId, Environment.MachineName, "batch run x", T0, start.AddDays(-3))),
                "remote" => SwarmJson.Line(new LockInfo(1, 1, "some-other-host-" + Guid.NewGuid().ToString("N"), "batch run x", T0, T0)),
                _ => "{\"schemaVer",
            });
        }

        File.SetLastWriteTimeUtc(file, stale ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow);
        var s = f.Assess(config);
        Assert.Equal(running, s.BatchRunning);
        Assert.Equal(running ? BlockerCodes.BatchRunning : BlockerCodes.RunUnfinished, Assert.Single(s.Blockers).Code);
    }

    [Fact]
    public void UnmergedTaskWorktree_BlocksWaivably()
    {
        using var f = new Fixture();
        var wt = new WorktreeManager(f.Paths, f.Config).Create(new CreateRequest("9934", "more", Epic, null));
        File.WriteAllText(Path.Combine(wt.Path, "more.txt"), "more\n");
        TempRepo.RunGit(wt.Path, "add", "-A");
        TempRepo.RunGit(wt.Path, "commit", "-q", "-m", "unlanded");
        var s = f.Assess();
        Assert.Equal((1, 1), (s.Worktrees, s.WorktreesUnmerged));
        var b = Assert.Single(s.Blockers);
        Assert.Equal((BlockerCodes.WorktreesUnmerged, true), (b.Code, b.Waivable));
        Assert.Contains("9934", b.Detail);
    }

    [Fact]
    public void EmptyCleanTaskWorktree_DoesNotBlock()
    {
        using var f = new Fixture();
        new WorktreeManager(f.Paths, f.Config).Create(new CreateRequest("9934", "more", Epic, null));
        var s = f.Assess();
        Assert.Equal((1, 0), (s.Worktrees, s.WorktreesUnmerged));
        Assert.True(s.ReadyToClose);
    }

    [Fact]
    public void TaskWorktreeThatCannotBeAssessed_Blocks()
    {
        using var f = new Fixture();
        var wt = new WorktreeManager(f.Paths, f.Config).Create(new CreateRequest("9934", "more", Epic, null));
        WorktreeManagerTests.CorruptGitFile(wt.Path);
        var s = f.Assess();
        Assert.Equal(1, s.WorktreesUnmerged);
        var b = Assert.Single(s.Blockers);
        Assert.Equal(BlockerCodes.WorktreesUnmerged, b.Code);
        Assert.Contains("could not be assessed", b.Detail);
    }

    [Fact]
    public void StaleRegistrationWhoseDirectoryExists_Blocks()
    {
        using var f = new Fixture();
        var wt = new WorktreeManager(f.Paths, f.Config).Create(new CreateRequest("9934", "more", Epic, null));

        // No commits (empty), but the directory holds uncommitted work git can no longer see.
        File.WriteAllText(Path.Combine(wt.Path, "work.txt"), "only copy\n");
        File.Delete(Path.Combine(wt.Path, ".git"));
        var s = f.Assess();
        Assert.Equal(1, s.WorktreesUnmerged);
        var b = Assert.Single(s.Blockers);
        Assert.Equal(BlockerCodes.WorktreesUnmerged, b.Code);
        Assert.Contains("stale", b.Detail);
    }

    [Fact]
    public void TrackedEdit_BlocksActiveDirty()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Repo.Root, "README.md"), "edited\n");
        var b = Assert.Single(f.Assess().Blockers);
        Assert.Equal((BlockerCodes.ActiveDirty, false), (b.Code, b.Waivable));
    }

    [Fact]
    public void UntrackedOnly_DoesNotBlock()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Repo.Root, "notes.txt"), "scratch\n");
        Assert.True(f.Assess().ReadyToClose);
    }

    [Fact]
    public void BehindUpstream_Blocks()
    {
        using var f = new Fixture();

        // Simulated upstream: a remote that is never contacted plus a local remote-tracking ref.
        f.Repo.Git("remote", "add", "origin", Path.Combine(f.Repo.Sandbox, "nowhere.git"));
        f.Repo.Git("checkout", "-q", "-b", "tmp");
        f.Repo.Commit("pushed by someone else", ("r.txt", "r\n"));
        var newer = f.Repo.Sha("tmp");
        f.Repo.Git("checkout", "-q", "main");
        f.Repo.Git("branch", "-D", "tmp");
        f.Repo.Git("update-ref", "refs/remotes/origin/main", newer);
        f.Repo.Git("config", "branch.main.remote", "origin");
        f.Repo.Git("config", "branch.main.merge", "refs/heads/main");

        var s = f.Assess();
        Assert.Equal("origin/main", s.Upstream);
        var b = Assert.Single(s.Blockers);
        Assert.Equal((BlockerCodes.ActiveBehindUpstream, false), (b.Code, b.Waivable));
        Assert.Contains("1 commit(s) behind 'origin/main'", b.Detail);
    }

    [Fact]
    public void ClosedEpic_ReportsEpicClosed()
    {
        using var f = new Fixture();
        var store = new EpicStore(new StateLayout(f.Repo.StateDir));
        store.Save(store.Get("42") with { State = EpicStates.Closed, MergedInto = "main", MergeCommit = "abc" });
        Assert.Contains(BlockerCodes.EpicClosed, Codes(f.Assess()));
    }

    [Fact]
    public void MissingInto_IsBadInput()
    {
        using var f = new Fixture();
        var epic = new EpicStore(new StateLayout(f.Repo.StateDir)).Get("42");
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => new EpicAssessor(f.Paths, f.Config).Assess(epic, "nope")).ExitCode);
    }
}
