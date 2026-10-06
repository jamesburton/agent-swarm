---
created: 2026-10-03
updated: 2026-10-05
status: current
---
# Worktree + Epic (Plan C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two `dnx` tools on top of Plan A's shared libraries: `worktree` (create, list and prune per-task worktrees that branch from an epic branch) and `epic` (open an epic branch, report its run state, close it with a `--no-ff` merge onto the active branch).

**Architecture:** One new library, `Swarm.Delivery`, holds everything both tools share: branch metadata stored in git config, a parser for `git worktree list --porcelain`, a reader for Plan A run state (`summary.json`, `returned.jsonl`, `events.jsonl`), the merged-work check, epic records, the worktree manager and pruner, and the epic opener, assessor and closer. Two thin `System.CommandLine` CLIs follow the pattern of the existing CLIs (`Swarm.TestGate.Cli`, `Swarm.Batch.Cli`, `Swarm.Squash.Cli`): `Swarm.Worktree.Cli` (package `Swarm.Worktree`, command `worktree`) and `Swarm.Epic.Cli` (package `Swarm.Epic`, command `epic`). Configuration stays in the shared `.swarm/batch.json`. This plan adds two sections to it, `worktree` and `epicTool`, and no other keys (Task 1).

**Tech Stack:** .NET 10 (`global.json` 10.0.401), C# with nullable, xUnit 2.9.3, System.CommandLine 2.0.0, System.Text.Json, git >= 2.31 (2.38+ to detect squash-landed content; see C5; developed and verified with git 2.54.0.windows.1, whose `git init` creates **reftable** repositories), NuGet tool packaging (`PackAsTool`).

**Spec:** [docs/specs/2026-10-02-agent-swarm-design.md](../specs/2026-10-02-agent-swarm-design.md) (sections 2 run state, 3 delivery mechanics, 5 packaging); [docs/workflow.md](../workflow.md) sections 2 (branch model) and 5 (tools); decisions: [docs/decisions.md](../decisions.md) (Stage 2 run state; "Testgate + batch production plan"; "Squash lander production plan"); dnx facts: [docs/dnx-invocation-notes.md](../dnx-invocation-notes.md). Shared library and conventions: sibling **Plan A** [2026-10-03-testgate-batch.md](2026-10-03-testgate-batch.md) (read its Global Constraints first) and its user reference [docs/batch-tools.md](../batch-tools.md). Sibling **Plan B** [2026-10-03-squash.md](2026-10-03-squash.md) (user reference [docs/squash-tool.md](../squash-tool.md)) is **merged first**: this plan is stacked on it (branch `swarm/worktree-epic` on `swarm/squash` @ `3b32d30`, which sits on `swarm/testgate-batch` @ `05dd5ce`). Plan C reads what Plan B writes (the `Ticket`/`Epic`/`Batch`/`Swarm-Run` trailers, the shared per-epic lock) but references no Plan B project. House style: [2026-10-03-swarm-definition-renderer.md](2026-10-03-swarm-definition-renderer.md).

## Global Constraints

Project-wide (every task). These are the same as Plan A's:

- Target `net10.0`; build and test with `-warnaserror` and **zero warnings** (xUnit analyzers included).
- Every library and CLI project imports `src/Swarm.Tooling.props`: **XML doc comments on every public API**, with `<param>`, `<returns>`, `<typeparam>` and `<exception>` where applicable (positional records document every parameter). Comments must be StyleCop-compatible.
- DRY: process, git, path-guard, JSON, retry, config and CLI-host logic come from Plan A's `Swarm.Git` / `Swarm.RunState` and are **never redefined**. Shared Plan C logic lives in `Swarm.Delivery`.
- **LF line endings**; JSON files are written with `SwarmJson.WriteFile`; stdout carries exactly one JSON line per command (`schemaVersion: 1` first, camelCase); progress goes to stderr; an error is exactly ONE stderr line, `error: <what> (<hint>)`.
- Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- `dnx`: never `--yes`; Git Bash uses `dnx.cmd`; tool args go after `--`; **bump `<Version>` on every re-pack**.
- Tests use `TempRepo`/`TempDir`/`TestConfig`/`JsonOutput` from `tests/Swarm.Tools.Tests/Support` (sandboxes under `SWARM_TEST_ROOT` or `%TEMP%\swt\<8 hex>`; with git 2.54 the repos are reftable, so ref lock files are not per-ref files; use `TempRepo.LockRef`, which handles both formats). **Tests never fetch, push or contact a remote**: an "upstream" is simulated with local refs (`refs/remotes/origin/main` plus `branch.main.remote/merge` config).

Rulings for this plan (cost-if-wrong in brackets):

- **C1 Prerequisite:** Plan A (all tasks plus its final-review fix wave) and Plan B are merged; this branch is stacked on both. They provide `Swarm.Git`, `Swarm.RunState` (including `Cli/CliHost.cs`, which holds `CliHost` and `CtrlCScope`, and `Cli/CommonOptions.cs`, which holds `CommonOptions` and `ToolContext`), `Swarm.Batching`, `Swarm.Squashing`, `tests/Swarm.Tools.Tests` and its `Support/` fixtures. Plan C reads the files batch writes (`BatchSummary`, `ReturnedEntry` and `RunEvent` from `Swarm.RunState`) and the trailers the squash lander writes. Plan C's **production** projects do not reference `Swarm.Gate`, `Swarm.Batching` or `Swarm.Squashing` (`Swarm.Squashing` depends on `Swarm.Batching`). Its **tests** may use `Swarm.Squashing` (`SquashMessage.Build`, `SquashRunner`), which the test project already references, so trailer parsing and the `squash run` interplay are checked against the real producer. Namespaces as built: `ExitCodes`, `ToolException`, `ToolErrors`, `GitRunner` (including `GitRunner.HeadsRef(branch)`, `WithIdentity`, `WithEnvironment`, `At`, `RefExists`, `RevParse`, `Lines`), `ProcessResult`, `RepoLocator`/`RepoPaths`, `SafeName`, `TextLines`, `SharedFile` and `FileTree` are in `Swarm.Git`; `StatePaths`, `StateLayout`, `SwarmConfig`, `ConfigOverrides`, `ConfigLoader`, `SquashConfig`, `SwarmJson`, `JsonlFile`, `ReturnLedger`, `RunDirectories`, `SlotSemaphore`/`SlotOptions`, `Progress`/`Verbosity` and the output records are in `Swarm.RunState`; `CliHost`, `CtrlCScope`, `CommonOptions` and `ToolContext` are in `Swarm.RunState.Cli`. Build every full ref with `GitRunner.HeadsRef(branch)`, never a `"refs/heads/" +` literal (DRY; the executed Plans A and B do the same).
- **C2 Config:** one shared file, `.swarm/batch.json` in the main worktree. This plan **adds only** two sections: `worktree` and `epicTool`. The section cannot be called `epic`, because Plan A already uses `"epic": "E1"` as the batch epic id string. Plan C reuses the top-level `worktreeRoot`, `baseBranch` (the active branch; the squash lander also uses it to bound its history scan), `stateDir` and `epicBranchTemplate`, and redefines none of them. Plan B landed first and added a top-level `lander` key and a `squash` section (`SwarmConfig.Lander`, `SwarmConfig.Squash`, and the `lander` rule plus `e.AddRange(SquashConfig.Check(c.Squash));` at the end of `ConfigLoader.Check`). Plan C's two additive hunks go **after** those: the properties after `Squash` (before the `[JsonIgnore] EpicBranch` property), the checks after the `SquashConfig.Check` line. The keys `worktree`/`epicTool` do not collide with `lander`/`squash`. The loader **rejects unknown keys**, and all three existing tools load `.swarm/batch.json`, so `Swarm.TestGate` 0.1.1, `Swarm.Batch` 0.2.0 and `Swarm.Squash` 0.1.0 reject a config that contains the new sections. Task 1 therefore bumps and re-packs **all three**: `Swarm.TestGate` 0.1.1 -> 0.1.2, `Swarm.Batch` 0.2.0 -> 0.2.1, `Swarm.Squash` 0.1.0 -> 0.1.1. [Old binaries fail loudly with `'worktree'`; they do not misbehave silently.]
- **C3 Branch naming:** each section has `branchTemplate`, `defaultKind` and `allowedPrefixes`. The placeholders are `{id}`, `{slug}` and `{kind}`. Defaults: `worktree.branchTemplate = "task/{id}-{slug}"` and `epicTool.branchTemplate = "epic/{id}-{slug}"`, with no prefix restriction. `allowedPrefixes` entries must end in `/` and are matched **case-sensitively**. Templates are checked when the config loads (exit 2), and every rendered branch is checked again before anything is created (exit 2), then validated with `git check-ref-format --branch`. The example-org Azure DevOps setup is `{"branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"]}` in both sections. Slugs are lowercase, hyphen-separated and at most 40 characters. [A repo with mixed-case prefixes must list each spelling.]
- **C4 Managed worktrees:** a worktree is "managed" when its branch has the git config entries `branch.<b>.swarm-ticket`, `branch.<b>.swarm-base` and `branch.<b>.swarm-fork-point`. They are written by `worktree create` and removed by git itself when `git branch -D` deletes the branch. Managed status is **never derived from paths**: git reports `C:/...` paths, and the temp directory can be an 8.3 short path. The path is `<worktreeRoot>/t-<ticket>` (`worktreeRoot` defaults to `<main parent>/<repo>-wt`, as in Plan A), guarded at 200 characters. Batch's `int-<epic>` worktree and the user's own worktrees are not managed, and prune never touches them. [A worktree made by hand on a `task/...` branch is invisible to prune.]
- **C5 Merged work:** a branch counts as landed when one of these is true. (1) **Ledger:** Plan A's run state recorded it landed: a `summary.json` `landed[].branch`, or a `returned.jsonl` entry with `final: rebased-and-landed`. (2) **Ancestor:** `merge-base --is-ancestor` against its base. (3) **Content:** `git merge-tree --write-tree <base> <branch>` yields the base's tree, which is how squash-landed work is detected. With git older than 2.38, merge-tree fails and the branch counts as **not** merged, which is the safe direction. A branch whose head equals its recorded fork point is **empty**, not merged. When the base branch is gone, the check runs against `baseBranch`. The Ledger check sees only `batch` runs: `squash run` writes no run state (no run folder, `summary.json`, `returned.jsonl` or `events.jsonl`), so a manual landing is found by the Ancestor or Content check only, and `batch` prunes finished run folders beyond `keepRuns` (default 20), so old ledger entries disappear too. For the same reason a task that `batch` returned and a human later landed with `squash run` would block `epic close` forever; `EpicAssessor` therefore drops a returned or unprocessed task whose branch `MergeCheck.LandedVia` reports landed on the epic (Task 8). [Content check costs one merge-tree per worktree.]
- **C6 Prune safety:** a locked worktree is **never** removed, not even with `--force`; unlock it with `git worktree unlock`. Dirty worktrees (including untracked files), unmerged worktrees, empty ones and ones with an abandoned base are removed only with `--force`. `--dry-run` changes nothing and reports what would happen. Merged branches are deleted with `git branch -D`, because `-d` refuses squash-landed branches; merge status is verified by C5 first. A worktree whose directory is gone has its metadata pruned with `git worktree prune`, which is repository-wide: git also drops metadata of other unlocked worktrees whose directories are missing. Its branch is deleted only when merged, or with `--force`. A removal that fails (Windows file lock) is reported per item, the run continues, and the exit code is 4. Observed with git 2.54 on Windows (with and without `--force`): `git worktree remove` exits 255 with `failed to delete '<path>': Invalid argument`, but it has already deleted the worktree's `.git` file and its registration, so the directory stays behind **unregistered** with the held file, and the branch and its `swarm-*` metadata stay. Later `list`/`prune` runs no longer see it; the user deletes the directory once the file is released and then deletes the branch (`git branch -D`). [Locked worktrees accumulate until a human unlocks them.]
- **C7 Epic records:** `<state>/epics/<id>.json` (`EpicRecord`, schema 1). `epic open` creates the branch from `baseBranch` (or `--from`) **without checking it out**. It is idempotent: the same id and slug on an existing open epic returns `created: false`. It also reports `batchEpic`, the value for `batch --epic` that maps back to this branch through `epicBranchTemplate`, or null with a warning. [A repo whose batch template cannot express the epic branch needs a changed `epicBranchTemplate`.]
- **C8 Close mechanics:** close merges `git merge --no-ff --no-edit -m <message>` in a tool-owned detached worktree `<worktreeRoot>/close-<id>`, using the user's identity, and **never squashes**. If the active branch is checked out in some worktree (normally the user's main worktree), that worktree is fast-forwarded with `merge --ff-only`, so its files follow. Otherwise the ref moves with compare-and-swap `update-ref`. The epic branch is kept unless `--delete-branch` is given (`git branch -d`). The tool never fetches or pushes: "behind upstream" compares against the **last fetched** upstream ref. [A stale fetch lets a close through; fetch before closing.]
- **C9 Close blockers** (`EpicBlocker.Code`, waivable with `--force` in brackets): `batch-running` [no] (a live holder of the per-epic lock `<state>/locks/batch-<batch epic id>`, which `batch run` and `squash run` share; the detail says `a batch or squash run holds epic '<id>'`), `run-unfinished` [yes], `tasks-returned` [yes], `worktrees-unmerged` [yes], `nothing-to-merge` [no], `active-dirty` (tracked changes only; untracked files do not count, because merge refuses to overwrite them anyway) [no], `active-behind-upstream` [no], `epic-closed` [no], `branch-missing` [no]. `epic status` reports the same blockers that `epic close` enforces, computed by one function (`EpicAssessor.Assess`). [A waived blocker is listed in `waived` in the JSON.]
- **C10 Exit codes:** Plan A's R1 values, unchanged. 0 ok; 1 work came back (close blocked or conflicted); 2 usage/config/path; 3 bad input (unknown or closed epic, existing branch, missing base); 4 environment (git, file locks, refs moved); 5 is unused here. `epic close` prints its JSON result for 0 and 1; for 2/3/4 there is no stdout.
- **C11 Package ids `Swarm.Worktree` and `Swarm.Epic` are placeholders and NOT REAL.** They are unclaimed on nuget.org, which is a dependency-confusion risk: anyone could publish under those names. Run them only with `--add-source <your feed>` until the human reserves an owned id prefix. The package `Description` says so.

## Review Focus

1. **Short or wrong-case CI prefixes** (`feat/`, `fix/`, `Feature/`, a template literal `feat/`, a prefix entry `feature` without the slash): the global instruction rule is that example-org pipelines match only the full `feature/` and `bugfix/` and silently skip anything else. Each case must be rejected with exit 2 and the CI hint, before any branch, worktree or record exists. Tests: `BranchTemplateTests.ShortKind_IsRejectedWithCiHint`, `WrongCasePrefix_IsRejected`, `BranchSectionConfigTests.TemplateOutsideAllowedPrefixes_IsUsage`, `PrefixWithoutSlash_IsUsage` (Task 1), `WorktreeManagerTests.DisallowedKind_CreatesNothing` (Task 4), `EpicOpenerTests.FixKind_CreatesNothing` (Task 7).
2. **Windows path quirks:** worktree root over 200 characters; a deep tracked file that would exceed MAX_PATH (259) inside the new worktree; `git worktree list` paths with forward slashes, different case or 8.3 names. Expected: exit 2 with nothing created; a warning in the JSON; worktrees identified by branch metadata so idempotent create and prune still work. Tests: `WorktreeListTests.Parse_ForwardSlashPathsLockReasonAndPrunable` (Task 2), `WorktreeManagerTests.LongRoot_Exit2_NothingCreated`, `DeepTrackedFile_WarnsAboutMaxPath`, `Create_IsIdempotentByBranch` (Task 4).
3. **Squash-landed work looks unmerged to git:** after Plan B squashes a ticket onto the epic, the task branch is not an ancestor, so a naive prune keeps it forever, and `git branch -d` refuses it. Expected: detected as merged through the content or the ledger, then removed with `-D`. Tests: `MergeCheckTests.SquashedContent_IsMergedViaContent`, `LedgerBranch_IsMergedViaLedger` (Task 3), `PrunerTests.MergedViaContent_RemovedAndBranchDeleted` (Task 5).
4. **A file held open inside a worktree** (testhost, an editor, a virus scanner) makes `git worktree remove` fail. Expected: that item is reported as failed, its branch is kept, other items continue, and the exit code is 4. Tests: `PrunerTests.HeldFile_FailsItemKeepsBranchContinues` (Task 5), `WorktreeCliTests.Prune_FailedItem_PrintsReportAndExits4` (Task 6).
5. **Active-branch states at close:** checked out in the user's main worktree, possibly dirty with tracked edits or only untracked files; checked out nowhere; behind its last-fetched upstream. Expected: a fast-forward of the checked-out worktree, a CAS ref update otherwise, or a non-waivable blocker. Tests: `EpicAssessorTests.TrackedEdit_BlocksActiveDirty`, `UntrackedOnly_DoesNotBlock`, `BehindUpstream_Blocks` (Task 8), `EpicCloserTests.ActiveCheckedOut_FastForwardsWorkingTree`, `ActiveNotCheckedOut_UpdatesRefOnly`, `Force_DoesNotWaiveActiveDirty` (Task 9).

## File Structure

| Path | Responsibility |
|---|---|
| `src/Swarm.RunState/SwarmConfig.cs` (modify) | Add `Worktree` and `EpicTool` section properties (after Plan B's `Squash`) |
| `src/Swarm.RunState/ConfigLoader.cs` (modify) | Validate the two sections (after Plan B's `SquashConfig.Check`) |
| `src/Swarm.TestGate.Cli/`, `src/Swarm.Batch.Cli/`, `src/Swarm.Squash.Cli/` `*.csproj` (modify) | Version bumps 0.1.2 / 0.2.1 / 0.1.1, because the strict loaders of the old packages reject the new keys (C2) |
| `src/Swarm.RunState/BranchSections.cs` | `IBranchNaming`, `WorktreeSection`, `EpicSection`, `BranchTemplate` (render, slug and prefix rules) |
| `src/Swarm.Delivery/` | `WorktreeList`, `BranchMeta`, `EpicStore`, `RunHistory`, `MergeCheck`, `WorktreeManager`, `Pruner`, `EpicNaming`, `EpicOpener`, `EpicAssessor`, `TrailerLog`, `MergeMessage`, `EpicCloser` |
| `src/Swarm.Worktree.Cli/` | `worktree create\|list\|prune` (package `Swarm.Worktree`, NOT REAL) |
| `src/Swarm.Epic.Cli/` | `epic open\|status\|close` (package `Swarm.Epic`, NOT REAL) |
| `tests/Swarm.Tools.Tests/Delivery/`, `RunState/`, `Cli/` | xUnit tests; `Support/RunStateFixture.cs` writes Plan A run-state files |
| `docs/worktree-epic-tools.md` | User reference for both tools |
| `docs/batch-tools.md`, `docs/squash-tool.md`, `docs/dnx-invocation-notes.md`, `docs/decisions.md`, `AGENTS.md`, `README.md` (modify) | New versions and config keys (Task 1), links, smoke record and decision entry (Task 10) |

---

### Task 1: Config extension: `worktree` and `epicTool` sections, branch templates

**Files:**
- Create: `src/Swarm.RunState/BranchSections.cs`
- Modify: `src/Swarm.RunState/SwarmConfig.cs` (two properties after Plan B's `Squash` property), `src/Swarm.RunState/ConfigLoader.cs` (two lines after Plan B's `SquashConfig.Check` line, just before `return e;`)
- Modify: `src/Swarm.TestGate.Cli/Swarm.TestGate.Cli.csproj`, `src/Swarm.Batch.Cli/Swarm.Batch.Cli.csproj`, `src/Swarm.Squash.Cli/Swarm.Squash.Cli.csproj` (`<Version>` only), `docs/batch-tools.md`, `docs/squash-tool.md` (versions, config rows)
- Test: `tests/Swarm.Tools.Tests/RunState/BranchTemplateTests.cs`, `tests/Swarm.Tools.Tests/RunState/BranchSectionConfigTests.cs`

**Interfaces:**
- Consumes: `SwarmConfig` (sealed record with `init` properties; the last data properties are `KeepRuns`, `Lander`, `Squash`, then the `[JsonIgnore] EpicBranch` computed property), `ConfigLoader.Parse(string json, string sourceName)`, `ConfigLoader.Validated(SwarmConfig config, string sourceName)` (throws the **first** error as `ToolException(Usage, "<source>: <error>", "see docs/batch-tools.md#configuration")`), `ConfigLoader.Check(SwarmConfig c) -> IReadOnlyList<string>` (strict JSON: unknown keys, `null` for non-nullable properties and wrong types are `invalid config: ...`), `ToolException` (`ExitCode`, `Message`, `ErrorLine`), `ExitCodes`, `SafeName.IsValid/Description` (all `Swarm.Git` / `Swarm.RunState`).
- Produces (namespace `Swarm.RunState`):
  - `interface IBranchNaming { string BranchTemplate { get; } string? DefaultKind { get; } IReadOnlyList<string>? AllowedPrefixes { get; } }`
  - `sealed record WorktreeSection : IBranchNaming` (defaults `"task/{id}-{slug}"`, `null`, `null`); `sealed record EpicSection : IBranchNaming` (defaults `"epic/{id}-{slug}"`, `null`, `null`).
  - `SwarmConfig.Worktree` (`WorktreeSection`, JSON `worktree`) and `SwarmConfig.EpicTool` (`EpicSection`, JSON `epicTool`).
  - `static partial class BranchTemplate { const int MaxSlugLength = 40; const string CiPrefixHint; static bool IsValidSlug(string? slug); static bool HasAllowedPrefix(string branch, IReadOnlyList<string>? allowed); static IReadOnlyList<string> Check(IBranchNaming naming, string section); static string Render(IBranchNaming naming, string id, string slug, string? kind); }`. `Render` throws `ToolException(Usage)` for an unsafe id, an invalid slug or kind, a `kind` given to a template without `{kind}`, or a disallowed prefix (with `CiPrefixHint`).

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/RunState/BranchTemplateTests.cs`:

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.RunState;

public class BranchTemplateTests
{
    static readonly WorktreeSection CiNaming = new()
    {
        BranchTemplate = "{kind}/{id}-{slug}",
        DefaultKind = "feature",
        AllowedPrefixes = ["feature/", "bugfix/"],
    };

    [Fact]
    public void Defaults_RenderTaskAndEpicBranches()
    {
        Assert.Equal("task/9933-login-form", BranchTemplate.Render(new WorktreeSection(), "9933", "login-form", null));
        Assert.Equal("epic/42-auth", BranchTemplate.Render(new EpicSection(), "42", "auth", null));
    }

    [Fact]
    public void CiTemplate_UsesDefaultKindOrGivenKind()
    {
        Assert.Equal("feature/9933-config-driven", BranchTemplate.Render(CiNaming, "9933", "config-driven", null));
        Assert.Equal("bugfix/9920-temp-files", BranchTemplate.Render(CiNaming, "9920", "temp-files", "bugfix"));
    }

    [Theory]
    [InlineData("fix")]
    [InlineData("feat")]
    [InlineData("hotfix")]
    public void ShortKind_IsRejectedWithCiHint(string kind)
    {
        var e = Assert.Throws<ToolException>(() => BranchTemplate.Render(CiNaming, "9933", "x", kind));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains($"branch '{kind}/9933-x' does not start with an allowed prefix (feature/, bugfix/)", e.Message);
        Assert.Contains("silently break CI", e.ErrorLine);
    }

    [Fact]
    public void WrongCasePrefix_IsRejected() =>
        Assert.False(BranchTemplate.HasAllowedPrefix("Feature/9933-x", ["feature/", "bugfix/"]));

    [Fact]
    public void NoRestriction_AllowsAnyPrefix()
    {
        Assert.True(BranchTemplate.HasAllowedPrefix("anything/x", null));
        Assert.True(BranchTemplate.HasAllowedPrefix("anything/x", []));
    }

    [Fact]
    public void KindForTemplateWithoutKind_IsUsage() =>
        Assert.Contains("has no {kind}", Assert.Throws<ToolException>(() => BranchTemplate.Render(new WorktreeSection(), "1", "x", "feature")).Message);

    [Theory]
    [InlineData("login-form", true)]
    [InlineData("a1-b2", true)]
    [InlineData("Login", false)]
    [InlineData("a--b", false)]
    [InlineData("-a", false)]
    [InlineData("a_b", false)]
    [InlineData("", false)]
    public void Slug_Rules(string slug, bool ok) => Assert.Equal(ok, BranchTemplate.IsValidSlug(slug));

    [Fact]
    public void LongSlug_IsInvalid() => Assert.False(BranchTemplate.IsValidSlug(new string('a', 41)));

    [Fact]
    public void UnsafeId_IsUsage() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => BranchTemplate.Render(new WorktreeSection(), "a b", "x", null)).ExitCode);
}
```

`tests/Swarm.Tools.Tests/RunState/BranchSectionConfigTests.cs`:

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.RunState;

public class BranchSectionConfigTests
{
    static SwarmConfig Load(string json) => ConfigLoader.Validated(ConfigLoader.Parse(json, "batch.json"), "batch.json");

    static ToolException Invalid(string json) => Assert.Throws<ToolException>(() => Load(json));

    [Fact]
    public void PlanAConfigWithoutSections_GetsDefaults()
    {
        var c = Load("""{ "slots": 2 }""");
        Assert.Equal("task/{id}-{slug}", c.Worktree.BranchTemplate);
        Assert.Equal("epic/{id}-{slug}", c.EpicTool.BranchTemplate);
    }

    [Fact]
    public void PartialSection_KeepsItsOwnDefaultTemplate()
    {
        var c = Load("""{ "worktree": { "allowedPrefixes": ["task/"] } }""");
        Assert.Equal("task/{id}-{slug}", c.Worktree.BranchTemplate);
        Assert.Equal(new[] { "task/" }, c.Worktree.AllowedPrefixes);
    }

    [Fact]
    public void CiConfig_IsValid()
    {
        var c = Load("""
            {
              "worktree": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"] },
              "epicTool": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"] },
            }
            """);
        Assert.Equal("feature", c.EpicTool.DefaultKind);
    }

    [Theory]
    [InlineData("""{ "worktree": { "branchTemplate": "feat/{id}-{slug}", "allowedPrefixes": ["feature/", "bugfix/"] } }""", "worktree.branchTemplate renders 'feat/1-x', which does not start with an allowed prefix")]
    [InlineData("""{ "epicTool": { "branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "fix", "allowedPrefixes": ["feature/", "bugfix/"] } }""", "epicTool.branchTemplate renders 'fix/1-x'")]
    public void TemplateOutsideAllowedPrefixes_IsUsage(string json, string expected)
    {
        var e = Invalid(json);
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void PrefixWithoutSlash_IsUsage() =>
        Assert.Contains("worktree.allowedPrefixes entries must end with '/' (got 'feature')", Invalid("""{ "worktree": { "allowedPrefixes": ["feature"] } }""").Message);

    [Theory]
    [InlineData("""{ "worktree": { "branchTemplate": "task/{id}" } }""", "worktree.branchTemplate must contain {id} and {slug}")]
    [InlineData("""{ "worktree": { "branchTemplate": "task/{id}-{slug}-{ticket}" } }""", "unknown placeholder '{ticket}'")]
    [InlineData("""{ "epicTool": { "branchTemplate": "{kind}/{id}-{slug}" } }""", "epicTool.defaultKind is required")]
    [InlineData("""{ "epicTool": { "defaultKind": "Feature" } }""", "epicTool.defaultKind must be lowercase letters")]
    [InlineData("""{ "worktree": { "branchTemplte": "x" } }""", "'branchTemplte'")]
    [InlineData("""{ "worktree": null }""", "invalid config")]
    public void InvalidSection_IsOneLineUsage(string json, string expected)
    {
        var e = Invalid(json);
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~BranchTemplateTests|FullyQualifiedName~BranchSectionConfigTests"`
Expected: FAIL to compile (`WorktreeSection`, `BranchTemplate`, `SwarmConfig.Worktree` not defined).

- [ ] **Step 3: Implement `BranchSections.cs`** (`src/Swarm.RunState/BranchSections.cs`)

```csharp
using System.Text.RegularExpressions;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Branch naming settings shared by the <c>worktree</c> and <c>epicTool</c> config sections.</summary>
public interface IBranchNaming
{
    /// <summary>Gets the template; placeholders <c>{id}</c>, <c>{slug}</c> and optional <c>{kind}</c>.</summary>
    string BranchTemplate { get; }

    /// <summary>Gets the kind used for <c>{kind}</c> when none is given (e.g. <c>feature</c>).</summary>
    string? DefaultKind { get; }

    /// <summary>Gets the allowed branch prefixes (each ends with '/'); null or empty allows any.</summary>
    IReadOnlyList<string>? AllowedPrefixes { get; }
}

// Separate record types (not one shared type) so a partial JSON section keeps its own default template:
// System.Text.Json builds a new instance from the type's defaults, not from the property initializer.

/// <summary>The <c>worktree</c> config section (task branches).</summary>
public sealed record WorktreeSection : IBranchNaming
{
    /// <inheritdoc/>
    public string BranchTemplate { get; init; } = "task/{id}-{slug}";

    /// <inheritdoc/>
    public string? DefaultKind { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<string>? AllowedPrefixes { get; init; }
}

/// <summary>The <c>epicTool</c> config section (epic branches). Not named <c>epic</c>: that key is the batch epic id.</summary>
public sealed record EpicSection : IBranchNaming
{
    /// <inheritdoc/>
    public string BranchTemplate { get; init; } = "epic/{id}-{slug}";

    /// <inheritdoc/>
    public string? DefaultKind { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<string>? AllowedPrefixes { get; init; }
}

/// <summary>Renders and validates branch names from an <see cref="IBranchNaming"/> section.</summary>
public static partial class BranchTemplate
{
    /// <summary>Longest accepted slug.</summary>
    public const int MaxSlugLength = 40;

    /// <summary>Hint attached to prefix errors.</summary>
    public const string CiPrefixHint = "example-org pipelines trigger only on the full words feature/ and bugfix/; feat/, fix/ and other short forms silently break CI";

    static readonly string[] Known = ["{id}", "{slug}", "{kind}"];

    /// <summary>Checks a slug: lowercase letters and digits in hyphen-separated words, at most <see cref="MaxSlugLength"/> chars.</summary>
    /// <param name="slug">The candidate.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidSlug(string? slug) => slug is { Length: > 0 and <= MaxSlugLength } && SlugPattern().IsMatch(slug);

    /// <summary>Checks a branch against the allowed prefixes (ordinal, case-sensitive).</summary>
    /// <param name="branch">Branch name.</param>
    /// <param name="allowed">Allowed prefixes; null or empty allows any.</param>
    /// <returns>True when allowed.</returns>
    public static bool HasAllowedPrefix(string branch, IReadOnlyList<string>? allowed) =>
        allowed is null || allowed.Count == 0 || allowed.Any(p => branch.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Lists configuration errors for one section.</summary>
    /// <param name="naming">The section.</param>
    /// <param name="section">Section name used in messages (<c>worktree</c> or <c>epicTool</c>).</param>
    /// <returns>Errors, empty when valid.</returns>
    public static IReadOnlyList<string> Check(IBranchNaming naming, string section)
    {
        var e = new List<string>();
        var t = naming.BranchTemplate;
        if (string.IsNullOrWhiteSpace(t) || !t.Contains("{id}", StringComparison.Ordinal) || !t.Contains("{slug}", StringComparison.Ordinal))
        {
            e.Add($"{section}.branchTemplate must contain {{id}} and {{slug}} (got '{t}')");
            return e;
        }

        foreach (Match m in Placeholder().Matches(t))
        {
            if (!Known.Contains(m.Value, StringComparer.Ordinal))
            {
                e.Add($"{section}.branchTemplate has unknown placeholder '{m.Value}' (allowed: {{id}}, {{slug}}, {{kind}})");
            }
        }

        if (naming.DefaultKind is { } k && !KindPattern().IsMatch(k))
        {
            e.Add($"{section}.defaultKind must be lowercase letters (got '{k}')");
        }
        else if (t.Contains("{kind}", StringComparison.Ordinal) && naming.DefaultKind is null)
        {
            e.Add($"{section}.defaultKind is required when branchTemplate uses {{kind}}");
        }

        foreach (var p in naming.AllowedPrefixes ?? [])
        {
            if (string.IsNullOrWhiteSpace(p) || !p.EndsWith('/') || p.Any(char.IsWhiteSpace))
            {
                e.Add($"{section}.allowedPrefixes entries must end with '/' (got '{p}')");
            }
        }

        if (e.Count == 0)
        {
            var sample = Fill(t, "1", "x", naming.DefaultKind);
            if (!HasAllowedPrefix(sample, naming.AllowedPrefixes))
            {
                e.Add($"{section}.branchTemplate renders '{sample}', which does not start with an allowed prefix ({string.Join(", ", naming.AllowedPrefixes!)}); {CiPrefixHint}");
            }
        }

        return e;
    }

    /// <summary>Renders a branch name.</summary>
    /// <param name="naming">The section (already validated by the config loader).</param>
    /// <param name="id">Ticket or epic id (a safe name).</param>
    /// <param name="slug">Slug (see <see cref="IsValidSlug"/>).</param>
    /// <param name="kind">Kind for <c>{kind}</c>, or null for the default.</param>
    /// <returns>The branch name.</returns>
    /// <exception cref="ToolException">Invalid id, slug or kind, or a disallowed prefix (exit code 2).</exception>
    public static string Render(IBranchNaming naming, string id, string slug, string? kind)
    {
        if (!SafeName.IsValid(id))
        {
            throw new ToolException(ExitCodes.Usage, $"id '{id}' is not a safe name ({SafeName.Description})");
        }

        if (!IsValidSlug(slug))
        {
            throw new ToolException(ExitCodes.Usage, $"slug '{slug}' must be lowercase letters and digits in hyphen-separated words, at most {MaxSlugLength} chars", "e.g. login-form");
        }

        if (kind is not null)
        {
            if (!naming.BranchTemplate.Contains("{kind}", StringComparison.Ordinal))
            {
                throw new ToolException(ExitCodes.Usage, $"--kind given but branchTemplate '{naming.BranchTemplate}' has no {{kind}}");
            }

            if (!KindPattern().IsMatch(kind))
            {
                throw new ToolException(ExitCodes.Usage, $"kind '{kind}' must be lowercase letters");
            }
        }

        var branch = Fill(naming.BranchTemplate, id, slug, kind ?? naming.DefaultKind);
        return HasAllowedPrefix(branch, naming.AllowedPrefixes)
            ? branch
            : throw new ToolException(ExitCodes.Usage, $"branch '{branch}' does not start with an allowed prefix ({string.Join(", ", naming.AllowedPrefixes!)})", CiPrefixHint);
    }

    static string Fill(string template, string id, string slug, string? kind) =>
        template.Replace("{id}", id, StringComparison.Ordinal).Replace("{slug}", slug, StringComparison.Ordinal).Replace("{kind}", kind ?? "", StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[a-z]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
```

- [ ] **Step 4: Add the section properties** (in `src/Swarm.RunState/SwarmConfig.cs`, inside `SwarmConfig`, between Plan B's `Squash` property and the `[JsonIgnore] EpicBranch` property)

Old:

```csharp
    /// <summary>Gets the squash lander settings.</summary>
    public SquashConfig Squash { get; init; } = new();

    /// <summary>Gets the epic branch name.</summary>
```

New:

```csharp
    /// <summary>Gets the squash lander settings.</summary>
    public SquashConfig Squash { get; init; } = new();

    /// <summary>Gets the <c>worktree</c> tool section (task branch naming; Plan C).</summary>
    public WorktreeSection Worktree { get; init; } = new();

    /// <summary>Gets the <c>epicTool</c> section (epic branch naming; Plan C).</summary>
    public EpicSection EpicTool { get; init; } = new();

    /// <summary>Gets the epic branch name.</summary>
```

- [ ] **Step 5: Validate them** (in `ConfigLoader.Check`, after Plan B's squash line, immediately before `return e;`)

Old:

```csharp
        e.AddRange(SquashConfig.Check(c.Squash));

        return e;
```

New:

```csharp
        e.AddRange(SquashConfig.Check(c.Squash));
        e.AddRange(BranchTemplate.Check(c.Worktree, "worktree"));
        e.AddRange(BranchTemplate.Check(c.EpicTool, "epicTool"));

        return e;
```

- [ ] **Step 6: Run to verify pass** (including the existing config tests: nothing they pin changes; `TestConfig.Write` serialises the whole `SwarmConfig`, so every CLI test now also round-trips the two new sections with their defaults)

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~BranchTemplateTests|FullyQualifiedName~BranchSectionConfigTests|FullyQualifiedName~ConfigLoaderTests|FullyQualifiedName~SquashConfigTests|FullyQualifiedName~SquashCliTests"`
Expected: all PASS (84 tests: 29 new, plus the existing 24 `ConfigLoaderTests`, 13 `SquashConfigTests` and 18 `SquashCliTests`).

- [ ] **Step 7: Bump and re-pack the three existing tools** (C2: their strict loaders reject `worktree`/`epicTool`)

Change only `<Version>`: `src/Swarm.TestGate.Cli/Swarm.TestGate.Cli.csproj` `0.1.1` -> `0.1.2`; `src/Swarm.Batch.Cli/Swarm.Batch.Cli.csproj` `0.2.0` -> `0.2.1`; `src/Swarm.Squash.Cli/Swarm.Squash.Cli.csproj` `0.1.0` -> `0.1.1`. Then:

```bash
cd <repo-root>
dotnet pack src/Swarm.TestGate.Cli -c Release -o .docs/feed -warnaserror
dotnet pack src/Swarm.Batch.Cli -c Release -o .docs/feed -warnaserror
dotnet pack src/Swarm.Squash.Cli -c Release -o .docs/feed -warnaserror
```

Expected: `Successfully created package` for `Swarm.TestGate.0.1.2.nupkg`, `Swarm.Batch.0.2.1.nupkg` and `Swarm.Squash.0.1.1.nupkg` (each also prints the known NU5039 missing-readme message).

Docs, exact edits:
- `docs/batch-tools.md` line 16: ``- `Swarm.TestGate` is version `0.1.1`; `Swarm.Batch` is `0.2.0` (squash became the default lander).`` -> ``- `Swarm.TestGate` is version `0.1.2`; `Swarm.Batch` is `0.2.1` (0.2.0 made squash the default lander; 0.1.2/0.2.1 only accept the `worktree` and `epicTool` config sections of the worktree and epic tools).``; lines 20-21: `Swarm.TestGate@0.1.1` -> `Swarm.TestGate@0.1.2` and `Swarm.Batch@0.2.0` -> `Swarm.Batch@0.2.1`.
- `docs/batch-tools.md` `## Configuration` table: after the `squash` row add this row (the link target is written in Task 6; the sweeper runs in Task 10):

  ```text
  | `worktree` / `epicTool` | see [worktree-epic-tools.md#branch-naming](worktree-epic-tools.md#branch-naming) | objects (`branchTemplate`, `defaultKind`, `allowedPrefixes`); read by the `worktree` and `epic` tools only | none |
  ```

- `docs/squash-tool.md` line 16: ``- Versions as built: `Swarm.Squash` 0.1.0, `Swarm.Batch` 0.2.0, `Swarm.TestGate` 0.1.1.`` -> ``- Versions as built: `Swarm.Squash` 0.1.1, `Swarm.Batch` 0.2.1, `Swarm.TestGate` 0.1.2 (bumped together when the `worktree` and `epicTool` config sections were added).``. Leave the historical run records (0.1.0/0.2.0) unchanged.

- [ ] **Step 8: Commit**

```bash
git add src tests docs/batch-tools.md docs/squash-tool.md
git commit -m "Add worktree and epicTool config sections with branch templates; bump testgate, batch and squash" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 2: Scaffold `Swarm.Delivery` and the two CLIs; worktree list parser, branch metadata, epic records

**Files:**
- Create: `src/Swarm.Delivery/Swarm.Delivery.csproj`, `src/Swarm.Delivery/WorktreeList.cs`, `src/Swarm.Delivery/BranchMeta.cs`, `src/Swarm.Delivery/EpicStore.cs`
- Create: `src/Swarm.Worktree.Cli/Swarm.Worktree.Cli.csproj`, `src/Swarm.Worktree.Cli/Program.cs`, `src/Swarm.Epic.Cli/Swarm.Epic.Cli.csproj`, `src/Swarm.Epic.Cli/Program.cs`
- Modify: `src/Swarm.sln`, `tests/Swarm.Tools.Tests/Swarm.Tools.Tests.csproj` (three project references); `tests/Swarm.Tools.Tests/Support/SquashFixture.cs`, `tests/Swarm.Tools.Tests/Squashing/SquashLanderTests.cs`, `SquashLanderEdgeTests.cs`, `TestedChainTests.cs` (rename `SquashFixture.Worktree` to `IntegrationFor`; see Step 1)
- Test: `tests/Swarm.Tools.Tests/Delivery/WorktreeListTests.cs`, `tests/Swarm.Tools.Tests/Delivery/BranchMetaTests.cs`, `tests/Swarm.Tools.Tests/Delivery/EpicStoreTests.cs`

**Interfaces:**
- Consumes: `GitRunner(string workingDirectory)`, `GitRunner.Run(params string[]) -> string` (trimmed stdout; non-zero exit is `ToolException(Environment)`), `GitRunner.Try(params string[]) -> ProcessResult(int ExitCode, string StdOut, string StdErr, bool Killed)`, `GitRunner.Lines(params string[])`, `TextLines.Split`, `ToolException(int exitCode, string message, string? hint = null)`, `ExitCodes`, `SafeName` (`Swarm.Git`); `StateLayout(string Root)` (`Swarm.RunState`); `SwarmJson.WriteFile<T>(path, value)` (atomic, LF) and `SwarmJson.Read<T>(path)` (throws `JsonException`, or `InvalidDataException` for JSON `null`); test fixtures `TempRepo.Create()`, `repo.Git(...)`, `repo.Sandbox`, `repo.Root`, `repo.Epic(from = "main", name = "epic/E1")`, `repo.Branch(name, from, files)`, `repo.Sha(rev)`, `TempDir.Dir` (`tests/Swarm.Tools.Tests/Support`).
- Produces (namespace `Swarm.Delivery`):
  - `sealed record GitWorktree(string Path, string? Head, string? Branch, bool Detached, bool Locked, string? LockReason, bool Prunable)` (`Branch` is the short name, without `refs/heads/`; `Path` is normalised with `Path.GetFullPath`).
  - `static class WorktreeList { static IReadOnlyList<GitWorktree> Parse(IEnumerable<string> porcelainLines); static IReadOnlyList<GitWorktree> Read(GitRunner git); static GitWorktree? CheckedOut(GitRunner git, string branch); static bool SamePath(string a, string b); }`
  - `sealed record BranchMeta(string Branch, string Ticket, string Base, string ForkPoint)`; `static class BranchMetaStore { static void Write(GitRunner git, BranchMeta meta); static IReadOnlyDictionary<string, BranchMeta> ReadAll(GitRunner git); }` (git config `branch.<b>.swarm-ticket|swarm-base|swarm-fork-point`).
  - `static class EpicStates { const string Open = "open"; const string Closed = "closed"; }`
  - `sealed record EpicRecord(int SchemaVersion, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, DateTime CreatedUtc, string State, DateTime? ClosedUtc, string? MergeCommit, string? MergedInto)`
  - `sealed class EpicStore(StateLayout state) { string Dir; string PathOf(string id); EpicRecord? Find(string id); EpicRecord Get(string id); void Save(EpicRecord record); IReadOnlyList<EpicRecord> All(); }`. An unsafe id gives `ToolException(Usage)`; `Get` of an unknown id gives `ToolException(BadInput, "epic '<id>' not found in <dir>", "open it first: epic open <id> <slug>")`; an unreadable file gives `ToolException(BadInput)`.
  - Project graph: `Delivery -> Git, RunState`; `Worktree.Cli -> Git, RunState, Delivery`; `Epic.Cli -> Git, RunState, Delivery`; `Tools.Tests -> + Delivery, Worktree.Cli, Epic.Cli`.

- [ ] **Step 1: Write the project files**

`src/Swarm.Delivery/Swarm.Delivery.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.Worktree.Cli/Swarm.Worktree.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>true</IsPackable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>worktree</ToolCommandName>
    <PackageId>Swarm.Worktree</PackageId>
    <Version>0.1.0</Version>
    <Description>NOT REAL placeholder package id (unclaimed on nuget.org; dependency-confusion risk). Per-task git worktrees from an epic branch for agent swarms.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
    <ProjectReference Include="..\Swarm.Delivery\Swarm.Delivery.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.Epic.Cli/Swarm.Epic.Cli.csproj`: identical except `<ToolCommandName>epic</ToolCommandName>`, `<PackageId>Swarm.Epic</PackageId>` and `<Description>NOT REAL placeholder package id (unclaimed on nuget.org; dependency-confusion risk). Open, report and close epic branches with --no-ff merges for agent swarms.</Description>` (the wording follows `Swarm.Squash.Cli.csproj`).

Placeholder entry points (replaced in Tasks 6 and 10): `src/Swarm.Worktree.Cli/Program.cs` below, and `src/Swarm.Epic.Cli/Program.cs` with the namespace `Swarm.Epic.Cli`:

```csharp
namespace Swarm.Worktree.Cli;

/// <summary>Entry point (placeholder until the CLI task).</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Always 2 until implemented.</returns>
    public static int Main(string[] args) => 2;
}
```

Add to the test csproj's project-reference `ItemGroup`, after the existing `Swarm.Squash.Cli` reference and before the `Swarm.FakeSuite` reference:

```xml
    <ProjectReference Include="..\..\src\Swarm.Delivery\Swarm.Delivery.csproj" />
    <ProjectReference Include="..\..\src\Swarm.Worktree.Cli\Swarm.Worktree.Cli.csproj" />
    <ProjectReference Include="..\..\src\Swarm.Epic.Cli\Swarm.Epic.Cli.csproj" />
```

Both CLI assemblies define `Program`, as the three existing CLIs (`Swarm.TestGate.Cli`, `Swarm.Batch.Cli`, `Swarm.Squash.Cli`) already do. Tests alias them the same way (`using WorktreeProgram = Swarm.Worktree.Cli.Program;`, like `using SquashProgram = Swarm.Squash.Cli.Program;`).

**Name clash to fix in the same step.** The new namespace `Swarm.Worktree` (from `Swarm.Worktree.Cli`) makes the simple name `Worktree` bind to that namespace inside every `Swarm.*` namespace, before `using static` members are considered. Plan B's tests call `SquashFixture.Worktree(repo)` through `using static Swarm.Tools.Tests.Support.SquashFixture;`, so referencing the new CLI breaks them with 33 `CS0118: 'Swarm.Worktree' is a namespace but is used like a variable` errors (seen in the scratch build). Rename the fixture method to `IntegrationFor` (declaration and every call; `IntegrationWorktree` itself is a type and is unaffected):

```bash
cd <repo-root>/tests/Swarm.Tools.Tests
sed -i -E 's/\bWorktree\(repo\)/IntegrationFor(repo)/g; s/public static IntegrationWorktree Worktree\(TempRepo repo\)/public static IntegrationWorktree IntegrationFor(TempRepo repo)/' Support/SquashFixture.cs Squashing/SquashLanderTests.cs Squashing/SquashLanderEdgeTests.cs Squashing/TestedChainTests.cs
grep -rn "\bWorktree(" --include=*.cs . | grep -v "IntegrationWorktree("   # expect no output
```

`Swarm.Epic` (from `Swarm.Epic.Cli`) has the same effect on a simple name `Epic`; no existing code uses one outside a type that declares its own `Epic` member (the tests' `const string Epic` fields and `TempRepo.Epic(...)` calls are member lookups and are unaffected). New code must not rely on a bare `Worktree` or `Epic` resolving to anything but these namespaces.

```bash
cd <repo-root>/src
dotnet sln Swarm.sln add Swarm.Delivery Swarm.Worktree.Cli Swarm.Epic.Cli
```

- [ ] **Step 2: Write the failing tests**

`tests/Swarm.Tools.Tests/Delivery/WorktreeListTests.cs`:

```csharp
using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class WorktreeListTests
{
    [Fact]
    public void Parse_ForwardSlashPathsLockReasonAndPrunable()
    {
        var lines = new[]
        {
            "worktree C:/Repos/app", "HEAD 1111111111111111111111111111111111111111", "branch refs/heads/main", "",
            "worktree C:/Repos/app-wt/t-9933", "HEAD 2222222222222222222222222222222222222222", "branch refs/heads/feature/9933-login", "locked agent is working", "",
            "worktree C:/Repos/app-wt/int-E1", "HEAD 3333333333333333333333333333333333333333", "detached", "",
            "worktree C:/Repos/app-wt/t-1", "HEAD 4444444444444444444444444444444444444444", "branch refs/heads/task/1-x", "locked", "prunable gitdir file points to non-existent location",
        };
        var list = WorktreeList.Parse(lines);
        Assert.Equal(4, list.Count);
        Assert.Equal(Path.GetFullPath("C:/Repos/app-wt/t-9933"), list[1].Path);
        Assert.Equal("feature/9933-login", list[1].Branch);
        Assert.True(list[1].Locked);
        Assert.Equal("agent is working", list[1].LockReason);
        Assert.True(list[2].Detached);
        Assert.Null(list[2].Branch);
        Assert.True(list[3].Locked && list[3].Prunable);
        Assert.Null(list[3].LockReason);
    }

    [Fact]
    public void Read_FindsLinkedWorktreeAndCheckedOutBranch()
    {
        using var repo = TempRepo.Create();
        var side = Path.Combine(repo.Sandbox, "side");
        repo.Git("worktree", "add", "-q", "-b", "side", side);
        var git = new GitRunner(repo.Root);
        Assert.Equal(2, WorktreeList.Read(git).Count);
        Assert.True(WorktreeList.SamePath(side, WorktreeList.CheckedOut(git, "side")!.Path));
        Assert.Null(WorktreeList.CheckedOut(git, "nope"));
    }

    [Fact]
    public void SamePath_IgnoresSeparatorsTrailingSlashAndCaseOnWindows()
    {
        Assert.True(WorktreeList.SamePath("C:/a/b/", Path.Combine("C:", "a", "b")));
        Assert.Equal(OperatingSystem.IsWindows(), WorktreeList.SamePath("C:/A/B", "C:/a/b"));
    }
}
```

`tests/Swarm.Tools.Tests/Delivery/BranchMetaTests.cs`:

```csharp
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
```

`tests/Swarm.Tools.Tests/Delivery/EpicStoreTests.cs`:

```csharp
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicStoreTests
{
    static EpicRecord Record(string id) =>
        new(1, id, "auth", $"epic/{id}-auth", "main", new string('a', 40), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), EpicStates.Open, null, null, null);

    [Fact]
    public void SaveFindAll()
    {
        using var dir = new TempDir();
        var store = new EpicStore(new StateLayout(dir.Dir));
        store.Save(Record("42"));
        store.Save(Record("7"));
        Assert.Equal("epic/42-auth", store.Get("42").Branch);
        Assert.Null(store.Find("99"));
        Assert.Equal(new[] { "42", "7" }, store.All().Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain('\r', File.ReadAllText(store.PathOf("42")));
    }

    [Fact]
    public void Get_Unknown_IsBadInputWithHint()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ToolException>(() => new EpicStore(new StateLayout(dir.Dir)).Get("42"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("epic open 42", e.ErrorLine);
    }

    [Fact]
    public void UnsafeId_IsUsage()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new EpicStore(new StateLayout(dir.Dir)).Find("../x")).ExitCode);
    }

    [Fact]
    public void CorruptFile_IsBadInput()
    {
        using var dir = new TempDir();
        var store = new EpicStore(new StateLayout(dir.Dir));
        Directory.CreateDirectory(store.Dir);
        File.WriteAllText(store.PathOf("42"), "{ not json");
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => store.Get("42")).ExitCode);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~WorktreeListTests|FullyQualifiedName~BranchMetaTests|FullyQualifiedName~EpicStoreTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement `WorktreeList`** (`src/Swarm.Delivery/WorktreeList.cs`)

```csharp
using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>One record of <c>git worktree list --porcelain</c>.</summary>
/// <param name="Path">Absolute path, normalised for this OS.</param>
/// <param name="Head">HEAD commit, if reported.</param>
/// <param name="Branch">Checked-out branch (short name), or null when detached.</param>
/// <param name="Detached">True for a detached HEAD.</param>
/// <param name="Locked">True when locked (<c>git worktree lock</c>).</param>
/// <param name="LockReason">Lock reason, if one was given.</param>
/// <param name="Prunable">True when git reports the worktree directory missing.</param>
public sealed record GitWorktree(string Path, string? Head, string? Branch, bool Detached, bool Locked, string? LockReason, bool Prunable);

/// <summary>Reads git's worktree list.</summary>
public static class WorktreeList
{
    const string BranchPrefix = "branch refs/heads/";

    /// <summary>Parses porcelain output (records start at each <c>worktree </c> line; blank lines are optional).</summary>
    /// <param name="porcelainLines">Output lines.</param>
    /// <returns>The worktrees in git's order.</returns>
    public static IReadOnlyList<GitWorktree> Parse(IEnumerable<string> porcelainLines)
    {
        var list = new List<GitWorktree>();
        GitWorktree? current = null;
        foreach (var raw in porcelainLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    list.Add(current);
                }

                current = new GitWorktree(System.IO.Path.GetFullPath(line["worktree ".Length..]), null, null, false, false, null, false);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                current = current with { Head = line["HEAD ".Length..] };
            }
            else if (line.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                current = current with { Branch = line[BranchPrefix.Length..] };
            }
            else if (line == "detached")
            {
                current = current with { Detached = true };
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                current = current with { Locked = true, LockReason = line.Length > "locked ".Length ? line["locked ".Length..] : null };
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                current = current with { Prunable = true };
            }
        }

        if (current is not null)
        {
            list.Add(current);
        }

        return list;
    }

    /// <summary>Runs <c>git worktree list --porcelain</c>.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <returns>The worktrees.</returns>
    public static IReadOnlyList<GitWorktree> Read(GitRunner git) => Parse(git.Lines("worktree", "list", "--porcelain"));

    /// <summary>Finds the worktree where a branch is checked out.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <param name="branch">Short branch name.</param>
    /// <returns>The worktree, or null.</returns>
    public static GitWorktree? CheckedOut(GitRunner git, string branch) =>
        Read(git).FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.Ordinal));

    /// <summary>Compares paths after normalisation (case-insensitive on Windows).</summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns>True when they name the same location.</returns>
    public static bool SamePath(string a, string b) =>
        string.Equals(
            System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'),
            System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
```

- [ ] **Step 5: Implement `BranchMeta`** (`src/Swarm.Delivery/BranchMeta.cs`)

```csharp
using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>What <c>worktree create</c> records about a task branch (stored in git config, removed by <c>git branch -D</c>).</summary>
/// <param name="Branch">Task branch (short name).</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Base">Branch it was created from (the epic branch).</param>
/// <param name="ForkPoint">Base commit at creation; head equal to it means "no commits yet".</param>
public sealed record BranchMeta(string Branch, string Ticket, string Base, string ForkPoint);

/// <summary>Reads and writes <see cref="BranchMeta"/> as <c>branch.&lt;b&gt;.swarm-*</c> config entries.</summary>
public static class BranchMetaStore
{
    const string TicketKey = "swarm-ticket";
    const string BaseKey = "swarm-base";
    const string ForkKey = "swarm-fork-point";

    /// <summary>Writes the metadata.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <param name="meta">The metadata.</param>
    public static void Write(GitRunner git, BranchMeta meta)
    {
        git.Run("config", $"branch.{meta.Branch}.{TicketKey}", meta.Ticket);
        git.Run("config", $"branch.{meta.Branch}.{BaseKey}", meta.Base);
        git.Run("config", $"branch.{meta.Branch}.{ForkKey}", meta.ForkPoint);
    }

    /// <summary>Reads the metadata of every branch that has all three entries.</summary>
    /// <param name="git">Runner in any worktree of the repo.</param>
    /// <returns>Branch name to metadata.</returns>
    public static IReadOnlyDictionary<string, BranchMeta> ReadAll(GitRunner git)
    {
        var r = git.Try("config", "--get-regexp", @"^branch\..*\.swarm-");
        if (r.ExitCode == 1)
        {
            return new Dictionary<string, BranchMeta>(StringComparer.Ordinal);
        }

        // Any other failure is reported by Run with git's one-line error.
        var lines = r.ExitCode == 0 ? TextLines.Split(r.StdOut) : git.Lines("config", "--get-regexp", @"^branch\..*\.swarm-");
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // "branch.<name>.<key> <value>"; git lower-cases the key but keeps the branch (subsection) as is.
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                continue;
            }

            var name = line[..space];
            var dot = name.LastIndexOf('.');
            var branch = name["branch.".Length..dot];
            var key = name[(dot + 1)..];
            if (!fields.TryGetValue(branch, out var map))
            {
                fields[branch] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            map[key] = line[(space + 1)..];
        }

        return fields
            .Where(kv => kv.Value.ContainsKey(TicketKey) && kv.Value.ContainsKey(BaseKey) && kv.Value.ContainsKey(ForkKey))
            .ToDictionary(kv => kv.Key, kv => new BranchMeta(kv.Key, kv.Value[TicketKey], kv.Value[BaseKey], kv.Value[ForkKey]), StringComparer.Ordinal);
    }
}
```

- [ ] **Step 6: Implement `EpicStore`** (`src/Swarm.Delivery/EpicStore.cs`)

```csharp
using System.Text.Json;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Epic lifecycle states.</summary>
public static class EpicStates
{
    /// <summary>Branch exists, work is landing.</summary>
    public const string Open = "open";

    /// <summary>Merged into the active branch.</summary>
    public const string Closed = "closed";
}

/// <summary>One epic, stored as <c>&lt;state&gt;/epics/&lt;id&gt;.json</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Id">Epic id (a safe name).</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="BaseBranch">Active branch it was opened from and closes into by default.</param>
/// <param name="BaseCommit">Base commit at open.</param>
/// <param name="CreatedUtc">When it was opened.</param>
/// <param name="State">An <see cref="EpicStates"/> value.</param>
/// <param name="ClosedUtc">When it was closed.</param>
/// <param name="MergeCommit">The <c>--no-ff</c> merge commit on the active branch.</param>
/// <param name="MergedInto">The branch it was merged into.</param>
public sealed record EpicRecord(
    int SchemaVersion, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, DateTime CreatedUtc,
    string State, DateTime? ClosedUtc, string? MergeCommit, string? MergedInto);

/// <summary>Epic records in the run-state directory.</summary>
/// <param name="state">State layout.</param>
public sealed class EpicStore(StateLayout state)
{
    /// <summary>Gets the records directory.</summary>
    public string Dir { get; } = Path.Combine(state.Root, "epics");

    /// <summary>Gets a record's file path.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ToolException">Unsafe id (exit code 2).</exception>
    public string PathOf(string id) =>
        SafeName.IsValid(id) ? Path.Combine(Dir, id + ".json") : throw new ToolException(ExitCodes.Usage, $"epic id '{id}' is not a safe name ({SafeName.Description})");

    /// <summary>Reads a record if it exists.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The record, or null.</returns>
    /// <exception cref="ToolException">Unsafe id (2) or unreadable file (3).</exception>
    public EpicRecord? Find(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return SwarmJson.Read<EpicRecord>(path);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new ToolException(ExitCodes.BadInput, $"epic file '{path}' is not valid: {e.Message}", "fix or delete it");
        }
    }

    /// <summary>Reads a record that must exist.</summary>
    /// <param name="id">Epic id.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ToolException">Unknown epic (exit code 3).</exception>
    public EpicRecord Get(string id) =>
        Find(id) ?? throw new ToolException(ExitCodes.BadInput, $"epic '{id}' not found in {Dir}", $"open it first: epic open {id} <slug>");

    /// <summary>Writes a record (atomic replace).</summary>
    /// <param name="record">The record.</param>
    public void Save(EpicRecord record) => SwarmJson.WriteFile(PathOf(record.Id), record);

    /// <summary>Reads every record.</summary>
    /// <returns>Records ordered by id.</returns>
    public IReadOnlyList<EpicRecord> All() =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*.json").Select(f => Get(Path.GetFileNameWithoutExtension(f))).OrderBy(r => r.Id, StringComparer.Ordinal).ToList()
            : [];
}
```

- [ ] **Step 7: Run to verify pass**

Run: `cd <repo-root> && dotnet build src/Swarm.sln -warnaserror && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~WorktreeListTests|FullyQualifiedName~BranchMetaTests|FullyQualifiedName~EpicStoreTests"`
Expected: 0 warnings (after the `SquashFixture` rename); all PASS (10 tests).

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "Scaffold Swarm.Delivery and worktree/epic CLIs; worktree list, branch metadata, epic records" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 3: Run-state reader and merged-work check

**Files:**
- Create: `src/Swarm.Delivery/RunHistory.cs`, `src/Swarm.Delivery/MergeCheck.cs`, `tests/Swarm.Tools.Tests/Support/RunStateFixture.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/RunHistoryTests.cs`, `tests/Swarm.Tools.Tests/Delivery/MergeCheckTests.cs`

**Interfaces:**
- Consumes (all as built in `Swarm.RunState` / `Swarm.Git`): `StateLayout.RunsDir` (`<state>/runs`; with the default `stateDir` `.docs/runs` the run folders are at `.docs/runs/runs/<run id>/`, the documented layout) and `StateLayout.RunDir(runId)`; `RunDirectories.SummaryFileName` (`summary.json`; a run folder is **finished iff that file exists**; batch writes it at the end of every run that got as far as creating its folder, including failed runs, and pre-run failures create no folder); `SwarmJson.Read/WriteFile`; `JsonlFile.Append/ReadAll<T>` (missing file = empty; malformed lines skipped); `ReturnLedger.ReadLatest(path)` (last record per task) and `ReturnLedger.New(taskId, branch, kind, stage, batch, reason)`; `BatchSummary` (30 positional fields in this order: `SchemaVersion, RunId, Epic, EpicBranch, Mode, Lander, ExitCode, Note, Tasks, TasksLanded, Returned, RebasedAndLanded, NeedsWorker, RejectedRed, BadInput, Unprocessed, FullSuiteRuns, BisectRuns, InferredRedSkipped, Batches, SizeTrace, WallSeconds, WaitMs, RunMs, Suites, Landed, BatchLog, DerivedTouches, ReturnedFile, EventsFile`); `LandedRecord(string Id, int Batch, string Commit, string Branch)` (`Branch` is the rebased copy `rebased/<epic>/<task>` for a task landed through its copy); `ReturnedEntry` (16 fields: `SchemaVersion, Utc, RunId, Task, Branch, Kind, Stage, Batch, ConflictingWith, Files, Reason, GitOutput, Rebase, RebasedBranch, RebaseOutput, Final`); `FinalState`, `ReturnKind`, `ReturnStage`; `RunEvent(int SchemaVersion, DateTime Utc, string RunId, string Type, object? Data)` and `EventTypes.RunStart` (batch writes `run-start` with data `{ tasks, epic, epicBranch, mode, lander }`; `Data` reads back as a `JsonElement`); `GitRunner`, `GitRunner.HeadsRef`, `TextLines`; fixtures `TempRepo`, `TempDir`.
- Produces (namespace `Swarm.Delivery`):
  - `static class TaskStates { const string Landed = "landed"; const string Returned = "returned"; const string Unprocessed = "unprocessed"; }`
  - `sealed record RunRecord(string RunId, DateTime StartedUtc, string? EpicBranch, BatchSummary? Summary, IReadOnlyDictionary<string, ReturnedEntry> Returned)` with `bool Finished`.
  - `sealed record TaskOutcome(string Task, string State, string Branch, string RunId, ReturnedEntry? LastReturn)` with `bool Blocking` (unprocessed, or returned with a final other than `no-op-after-rebase`).
  - `sealed class RunHistory { IReadOnlyList<RunRecord> Runs; static RunHistory Load(StateLayout layout); IReadOnlyList<RunRecord> ForEpic(string epicBranch); static IReadOnlyDictionary<string, TaskOutcome> Outcomes(IEnumerable<RunRecord> runsInOrder); IReadOnlySet<string> LandedBranches(); }`. Runs are ordered by the `run-start` event time; an unreadable `summary.json` counts as unfinished; a run's epic branch comes from its summary, or else from its `run-start` event data.
  - `static class MergeVia { const string Ledger = "ledger"; const string Ancestor = "ancestor"; const string Content = "content"; }`; `static class MergeCheck { static string? LandedVia(GitRunner git, string branch, string target, IReadOnlySet<string> ledgerBranches); }`.
  - Test-only (namespace `Swarm.Tools.Tests.Support`): `static class RunStateFixture { static string WriteRun(string stateDir, string runId, DateTime startedUtc, string epicBranch, IReadOnlyList<LandedRecord> landed, IReadOnlyList<ReturnedEntry> returned, IReadOnlyList<string>? unprocessed = null, bool finished = true, int exitCode = -1); static ReturnedEntry Returned(string task, string branch, string final, string kind = ReturnKind.Red); }` (exitCode -1 = 0 when nothing is returned or unprocessed, else 1).

- [ ] **Step 1: Write the fixture** (`tests/Swarm.Tools.Tests/Support/RunStateFixture.cs`)

```csharp
using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

/// <summary>Writes run-state files in Plan A's formats, as batch would.</summary>
public static class RunStateFixture
{
    public static ReturnedEntry Returned(string task, string branch, string final, string kind = ReturnKind.Red) =>
        ReturnLedger.New(task, branch, kind, ReturnStage.Suite, 1, "test") with { Final = final };

    public static string WriteRun(
        string stateDir, string runId, DateTime startedUtc, string epicBranch, IReadOnlyList<LandedRecord> landed,
        IReadOnlyList<ReturnedEntry> returned, IReadOnlyList<string>? unprocessed = null, bool finished = true, int exitCode = -1)
    {
        var dir = new StateLayout(stateDir).RunDir(runId);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var events = Path.Combine(dir, "events.jsonl");
        var returnedFile = Path.Combine(dir, "returned.jsonl");
        // The same run-start payload shape as BatchEngine.Run.
        JsonlFile.Append(events, new RunEvent(1, startedUtc, runId, EventTypes.RunStart, new { tasks = landed.Count + returned.Count, epic = "E1", epicBranch, mode = "batched", lander = "squash" }));
        foreach (var r in returned)
        {
            JsonlFile.Append(returnedFile, r with { RunId = runId, Utc = startedUtc });
        }

        if (finished)
        {
            unprocessed ??= [];
            var exit = exitCode >= 0 ? exitCode : returned.Count + unprocessed.Count == 0 ? 0 : 1;
            var summary = new BatchSummary(
                1, runId, "E1", epicBranch, "batched", "squash", exit, null,
                landed.Count + returned.Count + unprocessed.Count, landed.Count, returned.Count, 0, 0, 0, 0, unprocessed,
                1, 0, 0, 1, [4], 1.0, 0, 0, [], landed, [], new Dictionary<string, IReadOnlyList<string>>(), returnedFile, events);
            SwarmJson.WriteFile(Path.Combine(dir, RunDirectories.SummaryFileName), summary);
        }

        return dir;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Swarm.Tools.Tests/Delivery/RunHistoryTests.cs`:

```csharp
using Swarm.Delivery;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class RunHistoryTests
{
    static readonly DateTime T0 = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Load_OrdersByStartAndFiltersByEpic()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "zz-late", T0.AddHours(1), "epic/42-auth", [new LandedRecord("T2", 1, "c2", "task/T2")], []);
        RunStateFixture.WriteRun(dir.Dir, "aa-early", T0, "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], []);
        RunStateFixture.WriteRun(dir.Dir, "other", T0, "epic/7-x", [], []);
        var history = RunHistory.Load(new StateLayout(dir.Dir));
        Assert.Equal(new[] { "aa-early", "zz-late" }, history.ForEpic("epic/42-auth").Select(r => r.RunId));
    }

    [Fact]
    public void Outcomes_LaterLandingClearsEarlierReturn()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [new LandedRecord("T1", 1, "c1", "task/T1")], [RunStateFixture.Returned("T3", "task/T3", FinalState.ReturnedRed)]);
        var once = RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs);
        Assert.True(once["T3"].Blocking);
        Assert.Equal(TaskStates.Landed, once["T1"].State);

        RunStateFixture.WriteRun(dir.Dir, "r2", T0.AddHours(1), "epic/42-auth", [new LandedRecord("T3", 1, "c3", "task/T3")], []);
        Assert.False(RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs)["T3"].Blocking);
    }

    [Fact]
    public void Outcomes_UnprocessedIsBlocking_NoOpIsNot()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [], [RunStateFixture.Returned("T2", "task/T2", FinalState.NoOpAfterRebase, ReturnKind.Conflict)], ["T4"]);
        var o = RunHistory.Outcomes(RunHistory.Load(new StateLayout(dir.Dir)).Runs);
        Assert.Equal(TaskStates.Unprocessed, o["T4"].State);
        Assert.True(o["T4"].Blocking);
        Assert.False(o["T2"].Blocking);
    }

    [Fact]
    public void UnfinishedRun_TakesEpicFromRunStartEvent()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(dir.Dir, "crashed", T0, "epic/42-auth", [], [], finished: false);
        var run = Assert.Single(RunHistory.Load(new StateLayout(dir.Dir)).ForEpic("epic/42-auth"));
        Assert.False(run.Finished);
        Assert.Equal(T0, run.StartedUtc);
    }

    [Fact]
    public void LandedBranches_IncludesWorkerBranchOfRebasedAndLanded()
    {
        using var dir = new TempDir();
        RunStateFixture.WriteRun(
            dir.Dir, "r1", T0, "epic/42-auth",
            [new LandedRecord("T1", 1, "c1", "task/T1"), new LandedRecord("T2", 2, "c2", "rebased/E1/T2")],
            [RunStateFixture.Returned("T2", "task/T2", FinalState.RebasedAndLanded, ReturnKind.Conflict)]);
        var branches = RunHistory.Load(new StateLayout(dir.Dir)).LandedBranches();
        Assert.Contains("task/T1", branches);
        Assert.Contains("task/T2", branches);
    }

    [Fact]
    public void MissingStateDir_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(RunHistory.Load(new StateLayout(Path.Combine(dir.Dir, "none"))).Runs);
    }

    [Fact]
    public void CorruptSummary_CountsAsUnfinished()
    {
        using var dir = new TempDir();
        var run = RunStateFixture.WriteRun(dir.Dir, "r1", T0, "epic/42-auth", [], []);
        File.WriteAllText(Path.Combine(run, RunDirectories.SummaryFileName), "{ broken");
        Assert.False(Assert.Single(RunHistory.Load(new StateLayout(dir.Dir)).Runs).Finished);
    }
}
```

`tests/Swarm.Tools.Tests/Delivery/MergeCheckTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~RunHistoryTests|FullyQualifiedName~MergeCheckTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement `RunHistory`** (`src/Swarm.Delivery/RunHistory.cs`)

```csharp
using System.Text.Json;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Where a task stands across batch runs (<see cref="TaskOutcome.State"/>).</summary>
public static class TaskStates
{
    /// <summary>Landed on the epic.</summary>
    public const string Landed = "landed";

    /// <summary>Returned by its latest run.</summary>
    public const string Returned = "returned";

    /// <summary>Neither landed nor returned: its run stopped early.</summary>
    public const string Unprocessed = "unprocessed";
}

/// <summary>One batch run read from <c>&lt;state&gt;/runs/&lt;runId&gt;</c>.</summary>
/// <param name="RunId">Run id (folder name).</param>
/// <param name="StartedUtc">Time of the <c>run-start</c> event (folder creation time when absent).</param>
/// <param name="EpicBranch">Epic branch from the summary or the <c>run-start</c> event.</param>
/// <param name="Summary">The summary, or null when the run has none or it is unreadable.</param>
/// <param name="Returned">Latest <c>returned.jsonl</c> record per task.</param>
public sealed record RunRecord(string RunId, DateTime StartedUtc, string? EpicBranch, BatchSummary? Summary, IReadOnlyDictionary<string, ReturnedEntry> Returned)
{
    /// <summary>Gets a value indicating whether the run wrote a readable summary.</summary>
    public bool Finished => Summary is not null;
}

/// <summary>A task's latest state across runs.</summary>
/// <param name="Task">Task id.</param>
/// <param name="State">A <see cref="TaskStates"/> value.</param>
/// <param name="Branch">The worker's branch, when known.</param>
/// <param name="RunId">The run that decided the state.</param>
/// <param name="LastReturn">The latest return record from that run, if any.</param>
public sealed record TaskOutcome(string Task, string State, string Branch, string RunId, ReturnedEntry? LastReturn)
{
    /// <summary>Gets a value indicating whether the task blocks closing its epic.</summary>
    public bool Blocking => State == TaskStates.Unprocessed || (State == TaskStates.Returned && LastReturn?.Final != FinalState.NoOpAfterRebase);
}

/// <summary>Reads Plan A batch run state (read-only).</summary>
public sealed class RunHistory
{
    RunHistory(IReadOnlyList<RunRecord> runs) => Runs = runs;

    /// <summary>Gets every run, oldest first.</summary>
    public IReadOnlyList<RunRecord> Runs { get; }

    /// <summary>Loads every run under the state directory.</summary>
    /// <param name="layout">State layout.</param>
    /// <returns>The history (empty when there are no runs).</returns>
    public static RunHistory Load(StateLayout layout)
    {
        if (!Directory.Exists(layout.RunsDir))
        {
            return new RunHistory([]);
        }

        var runs = new List<RunRecord>();
        foreach (var dir in new DirectoryInfo(layout.RunsDir).GetDirectories())
        {
            var start = JsonlFile.ReadAll<RunEvent>(Path.Combine(dir.FullName, "events.jsonl")).FirstOrDefault(e => e.Type == EventTypes.RunStart);
            var summary = ReadSummary(Path.Combine(dir.FullName, RunDirectories.SummaryFileName));
            var epic = summary?.EpicBranch ?? (start?.Data is JsonElement { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("epicBranch", out var b) ? b.GetString() : null);
            runs.Add(new RunRecord(dir.Name, start?.Utc ?? dir.CreationTimeUtc, epic, summary, ReturnLedger.ReadLatest(Path.Combine(dir.FullName, "returned.jsonl"))));
        }

        return new RunHistory(runs.OrderBy(r => r.StartedUtc).ThenBy(r => r.RunId, StringComparer.Ordinal).ToList());
    }

    /// <summary>Folds runs (oldest first) into each task's latest outcome; within a run, landing wins over returning.</summary>
    /// <param name="runsInOrder">Runs, oldest first.</param>
    /// <returns>Task id to outcome.</returns>
    public static IReadOnlyDictionary<string, TaskOutcome> Outcomes(IEnumerable<RunRecord> runsInOrder)
    {
        var map = new Dictionary<string, TaskOutcome>(StringComparer.Ordinal);
        foreach (var run in runsInOrder)
        {
            foreach (var r in run.Returned.Values)
            {
                map[r.Task] = new TaskOutcome(r.Task, TaskStates.Returned, r.Branch, run.RunId, r);
            }

            if (run.Summary is not { } s)
            {
                continue;
            }

            foreach (var id in s.Unprocessed.Where(id => !run.Returned.ContainsKey(id)))
            {
                map[id] = new TaskOutcome(id, TaskStates.Unprocessed, map.GetValueOrDefault(id)?.Branch ?? "", run.RunId, null);
            }

            foreach (var l in s.Landed)
            {
                var r = run.Returned.GetValueOrDefault(l.Id);
                map[l.Id] = new TaskOutcome(l.Id, TaskStates.Landed, r?.Branch ?? l.Branch, run.RunId, r);
            }
        }

        return map;
    }

    /// <summary>Gets the runs of one epic branch.</summary>
    /// <param name="epicBranch">Epic branch name.</param>
    /// <returns>Matching runs, oldest first.</returns>
    public IReadOnlyList<RunRecord> ForEpic(string epicBranch) =>
        Runs.Where(r => string.Equals(r.EpicBranch, epicBranch, StringComparison.Ordinal)).ToList();

    /// <summary>Gets every branch recorded as landed: landed branches plus worker branches of rebased-and-landed tasks.</summary>
    /// <returns>Branch names.</returns>
    public IReadOnlySet<string> LandedBranches()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in Runs)
        {
            set.UnionWith(run.Summary?.Landed.Select(l => l.Branch) ?? []);
            set.UnionWith(run.Returned.Values.Where(r => r.Final == FinalState.RebasedAndLanded).Select(r => r.Branch));
        }

        return set;
    }

    static BatchSummary? ReadSummary(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return SwarmJson.Read<BatchSummary>(path);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
        {
            // Unreadable (torn or hand-edited): treated as unfinished, which blocks a close until someone looks.
            return null;
        }
    }
}
```

- [ ] **Step 5: Implement `MergeCheck`** (`src/Swarm.Delivery/MergeCheck.cs`)

```csharp
using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>How a branch was found to be landed.</summary>
public static class MergeVia
{
    /// <summary>Batch run state recorded it landed.</summary>
    public const string Ledger = "ledger";

    /// <summary>Its tip is an ancestor of the target.</summary>
    public const string Ancestor = "ancestor";

    /// <summary>Merging it into the target changes nothing (e.g. squash-landed).</summary>
    public const string Content = "content";
}

/// <summary>Decides whether a branch's work is already on a target branch.</summary>
public static class MergeCheck
{
    /// <summary>Checks ledger, ancestry, then content (merge-tree; git &gt;= 2.38, otherwise treated as not merged).</summary>
    /// <param name="git">Runner in any worktree.</param>
    /// <param name="branch">Branch to check (short name).</param>
    /// <param name="target">Target branch (short name).</param>
    /// <param name="ledgerBranches">Branches recorded as landed by batch runs.</param>
    /// <returns>A <see cref="MergeVia"/> value, or null when not merged.</returns>
    public static string? LandedVia(GitRunner git, string branch, string target, IReadOnlySet<string> ledgerBranches)
    {
        if (ledgerBranches.Contains(branch))
        {
            return MergeVia.Ledger;
        }

        var b = GitRunner.HeadsRef(branch);
        var t = GitRunner.HeadsRef(target);
        if (git.Try("merge-base", "--is-ancestor", b, t).ExitCode == 0)
        {
            return MergeVia.Ancestor;
        }

        // Exit 1 = conflicts; exit 129 = old git without --write-tree. Both mean "not provably merged".
        var merged = git.Try("merge-tree", "--write-tree", t, b);
        return merged.ExitCode == 0 && TextLines.Split(merged.StdOut).FirstOrDefault() == git.Run("rev-parse", t + "^{tree}")
            ? MergeVia.Content
            : null;
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~RunHistoryTests|FullyQualifiedName~MergeCheckTests"`
Expected: all PASS (12 tests).

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Read batch run state and detect merged, squashed or ledger-landed branches" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 4: Worktree manager: create and list

**Files:**
- Create: `src/Swarm.Delivery/WorktreeManager.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/WorktreeManagerTests.cs`

**Interfaces:**
- Consumes: `RepoPaths(WorktreeRoot, MainWorktreeRoot, CommonGitDir)`, `RepoLocator.Locate(dir)`, `GitRunner` (`RefExists(fullRef)`, `RevParse(rev)`, `At(dir)`, `Lines`, static `HeadsRef(branch)`), `ToolException`, `ExitCodes`, `TextLines` (`Swarm.Git`); `StatePaths.Resolve(RepoPaths, string)`, `StatePaths.ResolveWorktreeRoot(RepoPaths, string?)` (default `<main parent>/<repo>-wt`), `StatePaths.Guard(string fullPath, string what)` (over 200 chars is `ToolException(Usage)`), `StateLayout`, `SwarmConfig` (`Swarm.RunState`); `BranchTemplate.Render`, `SwarmConfig.Worktree` (Task 1); `WorktreeList`, `GitWorktree`, `BranchMeta`, `BranchMetaStore` (Task 2); `RunHistory`, `MergeCheck` (Task 3); `TempRepo` (`WorktreeRoot` = `<sandbox>/wt`, `StateDir` = `<sandbox>/state`), `TestConfig.For(repo)` (sets `StateDir` and `WorktreeRoot` to the sandbox) (test support). Batch's own integration worktree `<worktreeRoot>/int-<batch epic>` lives in the same root; it is detached and has no `swarm-*` metadata, so it is never managed.
- Produces (namespace `Swarm.Delivery`):
  - `sealed record CreateRequest(string Ticket, string Slug, string BaseBranch, string? Kind)`.
  - `sealed record WorktreeCreateResult(int SchemaVersion, bool Created, string Path, string Branch, string Ticket, string Base, string Head, IReadOnlyList<string> Warnings)` (stdout of `worktree create`).
  - `sealed record WorktreeEntry(string Path, string Branch, string? Ticket, string? Base, bool BaseExists, string? Head, bool Locked, string? LockReason, bool Missing, bool Dirty, bool Empty, int AheadOfBase, string? MergedVia, bool Managed)`.
  - `sealed record WorktreeListResult(int SchemaVersion, string Root, IReadOnlyList<WorktreeEntry> Worktrees)` (stdout of `worktree list`).
  - `sealed class WorktreeManager(RepoPaths repo, SwarmConfig config)` with `const int WindowsMaxPath = 259`, `string Root`, `GitRunner Git` (main worktree), `StateLayout State`, `string PathFor(string ticket)` (`<Root>/t-<ticket>`, guarded), `WorktreeCreateResult Create(CreateRequest request)`, `IReadOnlyList<WorktreeEntry> List(string? baseBranch = null, bool all = false)`.
  - `Create` checks in this order, so that a failure creates nothing: render and prefix (2), `check-ref-format` (2), path guard (2), base exists (3), branch already in a worktree (same base: idempotent `Created = false`; other base: 3), branch exists without a worktree (3), locked registration at the path (3), non-empty foreign directory (4). Then: prune a stale missing registration at that path, `git worktree add -q -b <branch> <path> <base sha>`, and write the metadata. The warning is added when `path + 1 + longest tracked path > 259`.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Delivery/WorktreeManagerTests.cs`)

```csharp
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
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~WorktreeManagerTests`
Expected: FAIL to compile (`WorktreeManager` not defined).

- [ ] **Step 3: Implement** (`src/Swarm.Delivery/WorktreeManager.cs`)

```csharp
using System.Globalization;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>A worktree to create.</summary>
/// <param name="Ticket">Ticket id (a safe name).</param>
/// <param name="Slug">Branch slug.</param>
/// <param name="BaseBranch">Branch to start from (normally the epic branch).</param>
/// <param name="Kind">Value for <c>{kind}</c>, or null for the configured default.</param>
public sealed record CreateRequest(string Ticket, string Slug, string BaseBranch, string? Kind);

/// <summary>stdout of <c>worktree create</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Created">False when the worktree already existed (idempotent repeat).</param>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Task branch.</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Base">Base branch.</param>
/// <param name="Head">Current head commit.</param>
/// <param name="Warnings">One-line warnings (e.g. MAX_PATH).</param>
public sealed record WorktreeCreateResult(int SchemaVersion, bool Created, string Path, string Branch, string Ticket, string Base, string Head, IReadOnlyList<string> Warnings);

/// <summary>A worktree with its swarm state.</summary>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Checked-out branch.</param>
/// <param name="Ticket">Ticket id (managed only).</param>
/// <param name="Base">Base branch (managed only).</param>
/// <param name="BaseExists">True when the base branch still exists.</param>
/// <param name="Head">Head commit.</param>
/// <param name="Locked">True when locked.</param>
/// <param name="LockReason">Lock reason.</param>
/// <param name="Missing">True when the directory is gone.</param>
/// <param name="Dirty">True with uncommitted or untracked changes.</param>
/// <param name="Empty">True when the head is still the fork point (no commits).</param>
/// <param name="AheadOfBase">Commits since the fork point.</param>
/// <param name="MergedVia">A <see cref="MergeVia"/> value, or null.</param>
/// <param name="Managed">True when created by this tool (branch metadata present).</param>
public sealed record WorktreeEntry(
    string Path, string Branch, string? Ticket, string? Base, bool BaseExists, string? Head, bool Locked, string? LockReason,
    bool Missing, bool Dirty, bool Empty, int AheadOfBase, string? MergedVia, bool Managed);

/// <summary>stdout of <c>worktree list</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Root">Worktree root.</param>
/// <param name="Worktrees">The worktrees.</param>
public sealed record WorktreeListResult(int SchemaVersion, string Root, IReadOnlyList<WorktreeEntry> Worktrees);

/// <summary>Creates and inspects per-task worktrees.</summary>
public sealed class WorktreeManager
{
    /// <summary>Longest path Windows tools without long-path support accept.</summary>
    public const int WindowsMaxPath = 259;

    readonly SwarmConfig config;

    /// <summary>Initializes a new instance of the <see cref="WorktreeManager"/> class.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="config">Validated config.</param>
    /// <exception cref="ToolException">State dir or worktree root path invalid (exit code 2).</exception>
    public WorktreeManager(RepoPaths repo, SwarmConfig config)
    {
        this.config = config;
        Git = new GitRunner(repo.MainWorktreeRoot);
        Root = StatePaths.ResolveWorktreeRoot(repo, config.WorktreeRoot);
        State = new StateLayout(StatePaths.Resolve(repo, config.StateDir));
    }

    /// <summary>Gets the worktree root.</summary>
    public string Root { get; }

    /// <summary>Gets a runner in the main worktree.</summary>
    public GitRunner Git { get; }

    /// <summary>Gets the run-state layout.</summary>
    public StateLayout State { get; }

    /// <summary>Gets the path for a ticket's worktree.</summary>
    /// <param name="ticket">Ticket id.</param>
    /// <returns><c>&lt;root&gt;/t-&lt;ticket&gt;</c>.</returns>
    /// <exception cref="ToolException">Too long (exit code 2).</exception>
    public string PathFor(string ticket) => StatePaths.Guard(Path.Combine(Root, "t-" + ticket), "task worktree");

    /// <summary>Creates a task worktree on a new branch from the base (idempotent for the same branch and base).</summary>
    /// <param name="request">The request.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">Naming or path (2), base/branch/lock state (3), foreign directory or git failure (4).</exception>
    public WorktreeCreateResult Create(CreateRequest request)
    {
        var branch = BranchTemplate.Render(config.Worktree, request.Ticket, request.Slug, request.Kind);
        if (Git.Try("check-ref-format", "--branch", branch).ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Usage, $"branch name '{branch}' is not valid for git");
        }

        var path = PathFor(request.Ticket);
        if (!Git.RefExists(GitRunner.HeadsRef(request.BaseBranch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"base branch '{request.BaseBranch}' not found", "open the epic first: epic open <id> <slug>");
        }

        var worktrees = WorktreeList.Read(Git);
        if (worktrees.FirstOrDefault(w => w.Branch == branch) is { } existing)
        {
            var meta = BranchMetaStore.ReadAll(Git).GetValueOrDefault(branch);
            return meta?.Base == request.BaseBranch
                ? new WorktreeCreateResult(SwarmJson.SchemaVersion, false, existing.Path, branch, request.Ticket, request.BaseBranch, existing.Head ?? "", [])
                : throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' is already checked out at '{existing.Path}' (base '{meta?.Base ?? "unknown"}')", "use another ticket or slug");
        }

        if (Git.RefExists(GitRunner.HeadsRef(branch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' already exists without a worktree", "delete it or use another slug");
        }

        if (worktrees.FirstOrDefault(w => WorktreeList.SamePath(w.Path, path)) is { } registered)
        {
            if (registered.Locked)
            {
                throw new ToolException(ExitCodes.BadInput, $"'{path}' is registered as a locked worktree ({registered.LockReason ?? "no reason given"})", "git worktree unlock it first");
            }

            if (!registered.Prunable)
            {
                throw new ToolException(ExitCodes.BadInput, $"'{path}' is already a worktree of branch '{registered.Branch ?? "(detached)"}'");
            }

            // A deleted directory with stale registration: drop the registration so the path can be reused.
            Git.Run("worktree", "prune");
        }

        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new ToolException(ExitCodes.Environment, $"'{path}' exists and is not empty", "remove it or change worktreeRoot");
        }

        // Start from the base's sha (not its name) so the fork point recorded below is exactly what was checked out.
        var forkPoint = Git.RevParse(GitRunner.HeadsRef(request.BaseBranch));
        Directory.CreateDirectory(Root);
        Git.Run("worktree", "add", "-q", "-b", branch, path, forkPoint);
        BranchMetaStore.Write(Git, new BranchMeta(branch, request.Ticket, request.BaseBranch, forkPoint));
        return new WorktreeCreateResult(SwarmJson.SchemaVersion, true, path, branch, request.Ticket, request.BaseBranch, forkPoint, MaxPathWarnings(path, forkPoint));
    }

    /// <summary>Lists worktrees with their state.</summary>
    /// <param name="baseBranch">Only managed worktrees on this base, or null for all bases.</param>
    /// <param name="all">Also include unmanaged worktrees on a branch (ignored when <paramref name="baseBranch"/> is set).</param>
    /// <returns>The entries in git's order.</returns>
    public IReadOnlyList<WorktreeEntry> List(string? baseBranch = null, bool all = false)
    {
        var metas = BranchMetaStore.ReadAll(Git);
        var ledger = RunHistory.Load(State).LandedBranches();
        var entries = new List<WorktreeEntry>();
        foreach (var w in WorktreeList.Read(Git).Where(w => w.Branch is not null))
        {
            var meta = metas.GetValueOrDefault(w.Branch!);
            if ((meta is null && (!all || baseBranch is not null)) || (baseBranch is not null && meta?.Base != baseBranch))
            {
                continue;
            }

            var missing = w.Prunable || !Directory.Exists(w.Path);
            var dirty = !missing && Git.At(w.Path).Run("status", "--porcelain").Length > 0;
            if (meta is null)
            {
                entries.Add(new WorktreeEntry(w.Path, w.Branch!, null, null, false, w.Head, w.Locked, w.LockReason, missing, dirty, false, 0, null, false));
                continue;
            }

            var baseExists = Git.RefExists(GitRunner.HeadsRef(meta.Base));
            var empty = string.Equals(w.Head, meta.ForkPoint, StringComparison.OrdinalIgnoreCase);
            var ahead = int.Parse(Git.Run("rev-list", "--count", $"{meta.ForkPoint}..{GitRunner.HeadsRef(w.Branch!)}"), CultureInfo.InvariantCulture);
            var via = empty ? null : MergeCheck.LandedVia(Git, w.Branch!, baseExists ? meta.Base : config.BaseBranch, ledger);
            entries.Add(new WorktreeEntry(w.Path, w.Branch!, meta.Ticket, meta.Base, baseExists, w.Head, w.Locked, w.LockReason, missing, dirty, empty, ahead, via, true));
        }

        return entries;
    }

    IReadOnlyList<string> MaxPathWarnings(string path, string commit)
    {
        var longest = Git.Lines("ls-tree", "-r", "--name-only", commit).Select(l => l.Length).DefaultIfEmpty(0).Max();
        var total = path.Length + 1 + longest;
        return total > WindowsMaxPath
            ? [$"deepest file path in the worktree will be {total} chars (> {WindowsMaxPath}); tools without long-path support may fail there; shorten worktreeRoot"]
            : [];
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~WorktreeManagerTests`
Expected: all PASS (14 tests).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add worktree manager: create from epic branch and list with merge state" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 5: Pruner (merged, missing, abandoned; dry-run, force, locked, held files)

**Files:**
- Create: `src/Swarm.Delivery/Pruner.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/PrunerTests.cs`

**Interfaces:**
- Consumes: `WorktreeManager.List/Git/Create`, `WorktreeEntry`, `CreateRequest` (Task 4); `MergeVia` (Task 3); `ProcessResult(int ExitCode, string StdOut, string StdErr, bool Killed)`, `TextLines.OneLine`, `FileTree.DeleteTree` (tests), `RepoLocator.Locate` (`Swarm.Git`); `SwarmJson` (`Swarm.RunState`).
- Produces (namespace `Swarm.Delivery`):
  - `static class PruneActions { const string Remove = "remove"; const string PruneMetadata = "prune-metadata"; const string Keep = "keep"; }`
  - `sealed record PruneDecision(string Action, string Reason, bool DeleteBranch)`.
  - `sealed record PruneItem(string Path, string Branch, string? Ticket, string Action, string Reason, bool Done, bool BranchDeleted, string? Error)`.
  - `sealed record PruneReport(int SchemaVersion, bool DryRun, bool Force, IReadOnlyList<PruneItem> Items, int Removed, int Kept, int Failed)` (stdout of `worktree prune`).
  - `sealed class Pruner(WorktreeManager manager) { static PruneDecision Decide(WorktreeEntry entry, bool force); PruneReport Prune(string? baseBranch, bool dryRun, bool force); }`. Decision order: locked -> keep (always); missing -> prune-metadata (delete the branch if merged, or if forced); then without force: dirty -> keep, merged -> remove, empty -> keep, unmerged -> keep (reason names "abandoned" when the base is gone); with force, every keep except locked becomes remove with reason `forced: <reason>`. Only managed entries are considered.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Delivery/PrunerTests.cs`)

```csharp
using Swarm.Delivery;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class PrunerTests
{
    const string Epic = "epic/42-auth";

    static WorktreeEntry Entry(bool locked = false, bool missing = false, bool dirty = false, bool empty = false, string? merged = null, bool baseExists = true) =>
        new("p", "task/1-x", "1", Epic, baseExists, "h", locked, locked ? "busy" : null, missing, dirty, empty, empty ? 0 : 2, merged, true);

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
        PruneReport report;
        using (new FileStream(Path.Combine(held.Path, "f1.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = new Pruner(m).Prune(null, false, false);
        }

        Assert.Equal((1, 1), (report.Removed, report.Failed));
        var failed = report.Items.Single(i => i.Error is not null);
        Assert.Equal(held.Branch, failed.Branch);
        Assert.False(failed.BranchDeleted);
        Assert.NotEmpty(repo.Git("branch", "--list", held.Branch));
        Assert.False(Directory.Exists(free.Path));

        // C6, observed with git 2.54: the failed remove already dropped the registration; the directory stays behind.
        Assert.True(Directory.Exists(held.Path));
        Assert.Empty(m.List());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~PrunerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** (`src/Swarm.Delivery/Pruner.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Prune actions.</summary>
public static class PruneActions
{
    /// <summary>Remove the worktree (and its branch when the decision says so).</summary>
    public const string Remove = "remove";

    /// <summary>The directory is gone: drop git's registration.</summary>
    public const string PruneMetadata = "prune-metadata";

    /// <summary>Leave it alone.</summary>
    public const string Keep = "keep";
}

/// <summary>What to do with one worktree.</summary>
/// <param name="Action">A <see cref="PruneActions"/> value.</param>
/// <param name="Reason">One-line reason.</param>
/// <param name="DeleteBranch">True to delete the task branch as well.</param>
public sealed record PruneDecision(string Action, string Reason, bool DeleteBranch);

/// <summary>One line of the prune report.</summary>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Task branch.</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Action">The decided action.</param>
/// <param name="Reason">Why.</param>
/// <param name="Done">True when the action was carried out (false for keep and for dry runs).</param>
/// <param name="BranchDeleted">True when the branch was deleted.</param>
/// <param name="Error">One-line failure, or null.</param>
public sealed record PruneItem(string Path, string Branch, string? Ticket, string Action, string Reason, bool Done, bool BranchDeleted, string? Error);

/// <summary>stdout of <c>worktree prune</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="DryRun">True when nothing was changed.</param>
/// <param name="Force">True when unmerged, dirty and empty worktrees were removed too.</param>
/// <param name="Items">One item per managed worktree considered.</param>
/// <param name="Removed">Worktrees removed or deregistered.</param>
/// <param name="Kept">Worktrees kept.</param>
/// <param name="Failed">Items whose action failed.</param>
public sealed record PruneReport(int SchemaVersion, bool DryRun, bool Force, IReadOnlyList<PruneItem> Items, int Removed, int Kept, int Failed);

/// <summary>Removes finished task worktrees; never unmerged work without force, never locked worktrees.</summary>
/// <param name="manager">The worktree manager.</param>
public sealed class Pruner(WorktreeManager manager)
{
    /// <summary>Decides what to do with one managed worktree.</summary>
    /// <param name="entry">The worktree.</param>
    /// <param name="force">Whether unmerged, dirty and empty work may go.</param>
    /// <returns>The decision.</returns>
    public static PruneDecision Decide(WorktreeEntry entry, bool force)
    {
        if (entry.Locked)
        {
            return new PruneDecision(PruneActions.Keep, $"locked ({entry.LockReason ?? "no reason"}): run git worktree unlock first", false);
        }

        if (entry.Missing)
        {
            var merged = entry.MergedVia is not null;
            var reason = merged ? $"directory missing; branch merged ({entry.MergedVia})" : force ? "directory missing; unmerged branch deleted (forced)" : "directory missing; unmerged branch kept";
            return new PruneDecision(PruneActions.PruneMetadata, reason, merged || force);
        }

        var normal = entry switch
        {
            { Dirty: true } => new PruneDecision(PruneActions.Keep, "uncommitted or untracked changes", false),
            { MergedVia: { } via } => new PruneDecision(PruneActions.Remove, $"merged ({via})", true),
            { Empty: true } => new PruneDecision(PruneActions.Keep, "no commits yet", false),
            { BaseExists: false } => new PruneDecision(PruneActions.Keep, $"abandoned: base '{entry.Base}' no longer exists and {entry.AheadOfBase} commit(s) are unmerged", false),
            _ => new PruneDecision(PruneActions.Keep, $"unmerged: {entry.AheadOfBase} commit(s) not on '{entry.Base}'", false),
        };
        return force && normal.Action == PruneActions.Keep ? new PruneDecision(PruneActions.Remove, "forced: " + normal.Reason, true) : normal;
    }

    /// <summary>Prunes managed worktrees.</summary>
    /// <param name="baseBranch">Only worktrees on this base, or null for all.</param>
    /// <param name="dryRun">Report only.</param>
    /// <param name="force">See <see cref="Decide"/>.</param>
    /// <returns>The report; failed items carry an error and do not stop the run.</returns>
    public PruneReport Prune(string? baseBranch, bool dryRun, bool force)
    {
        var git = manager.Git;
        var items = new List<PruneItem>();
        var metadataPruned = false;
        foreach (var e in manager.List(baseBranch))
        {
            var d = Decide(e, force);
            var item = new PruneItem(e.Path, e.Branch, e.Ticket, d.Action, d.Reason, false, false, null);
            if (dryRun || d.Action == PruneActions.Keep)
            {
                items.Add(item);
                continue;
            }

            ProcessResult r;
            if (d.Action == PruneActions.Remove)
            {
                r = force ? git.Try("worktree", "remove", "--force", e.Path) : git.Try("worktree", "remove", e.Path);
            }
            else if (metadataPruned)
            {
                r = new ProcessResult(0, "", "", false);
            }
            else
            {
                // Repository-wide: also drops registrations of other unlocked worktrees whose directory is gone.
                r = git.Try("worktree", "prune");
                metadataPruned = true;
            }

            if (r.ExitCode != 0)
            {
                items.Add(item with { Error = TextLines.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut) });
                continue;
            }

            item = item with { Done = true };
            if (d.DeleteBranch)
            {
                // -D, not -d: merge state was verified above, and -d refuses squash-landed branches.
                var b = git.Try("branch", "-D", e.Branch);
                item = b.ExitCode == 0 ? item with { BranchDeleted = true } : item with { Error = "branch delete failed: " + TextLines.OneLine(b.StdErr) };
            }

            items.Add(item);
        }

        return new PruneReport(
            SwarmJson.SchemaVersion, dryRun, force, items,
            items.Count(i => i.Done), items.Count(i => i.Action == PruneActions.Keep), items.Count(i => i.Error is not null));
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~PrunerTests`
Expected: all PASS (10 tests; `HeldFile_...` returns early off Windows).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add worktree pruner with dry-run, force and locked/held-file safety" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 6: `worktree` CLI, packaging, and its documentation

**Files:**
- Modify: `src/Swarm.Worktree.Cli/Program.cs` (replace the placeholder)
- Create: `docs/worktree-epic-tools.md` (worktree half)
- Test: `tests/Swarm.Tools.Tests/Cli/WorktreeCliTests.cs`

**Interfaces:**
- Consumes: `Swarm.RunState.Cli`: `CommonOptions` (`AddTo(Command)` adds `--config`, `--state`, `--slots`, `--max-wait`, `--verbosity`; `Resolve(ParseResult, string currentDirectory, ConfigOverrides extra) -> ToolContext`), `ToolContext(RepoPaths Repo, SwarmConfig Config, StateLayout State, Verbosity Verbosity)`, `CliHost.Invoke(RootCommand, string[], TextWriter, TextWriter)` (parse errors are one `error: ... (see --help)` line and exit 2; `ToolException`s go through `ToolErrors.Handle`); `ConfigOverrides.None`, `Progress(TextWriter, Verbosity).Warn`, `SwarmJson.Line`, `StateLayout` (`Swarm.RunState`); `ToolException.Format(message, hint)` (`Swarm.Git`); `EpicStore`, `EpicStates` (Task 2); `WorktreeManager`, `CreateRequest`, `WorktreeListResult` (Task 4); `Pruner`, `PruneReport` (Task 5); test helper `JsonOutput.SingleJsonLine(stdout)` (`tests/Swarm.Tools.Tests/Support/JsonOutput.cs`). No `CtrlCScope`: these commands run a few short git calls and need no cancellation.
- Produces:
  - `worktree create <ticket> <slug> (--epic <id> | --base <branch>) [--kind <k>] [common]` -> one `WorktreeCreateResult` line; warnings go to stderr as `warning: ...`; exit 0. `--epic` resolves the branch from `<state>/epics/<id>.json` and refuses a closed epic (3).
  - `worktree list [--epic <id> | --base <branch>] [--all] [common]` -> one `WorktreeListResult` line; exit 0.
  - `worktree prune [--epic <id> | --base <branch>] [--dry-run] [--force] [common]` -> one `PruneReport` line; exit 0, or 4 when any item failed (then stderr also gets `error: <n> worktree(s) could not be pruned (see items[].error)`).
  - `--epic` and `--base` together -> exit 2. `[common]` is Plan A's `CommonOptions`: `--slots` and `--max-wait` are accepted and ignored.
  - `public static int Swarm.Worktree.Cli.Program.Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)`.
  - `internal static class Swarm.Worktree.Cli.BaseOption { static string? Resolve(ParseResult p, Option<string?> epic, Option<string?> baseBranch, StateLayout state, bool required, bool allowClosed); }`

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Cli/WorktreeCliTests.cs`)

```csharp
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using WorktreeProgram = Swarm.Worktree.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class WorktreeCliTests
{
    const string Epic = "epic/42-auth";

    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = WorktreeProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static (TempRepo Repo, string Config) Setup(string state = EpicStates.Open)
    {
        var repo = TempRepo.Create();
        repo.Epic(name: Epic);
        new EpicStore(new StateLayout(repo.StateDir)).Save(new EpicRecord(1, "42", "auth", Epic, "main", repo.Sha("main"), DateTime.UtcNow, state, null, null, null));
        return (repo, TestConfig.Write(repo, TestConfig.For(repo)));
    }

    [Fact]
    public void Create_WithEpic_PrintsResult()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        var (code, output, _) = Run(repo, "create", "9933", "login-form", "--epic", "42", "--config", config);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("task/9933-login-form", json.GetProperty("branch").GetString());
        Assert.Equal(Epic, json.GetProperty("base").GetString());
        Assert.True(json.GetProperty("created").GetBoolean());
    }

    [Fact]
    public void Create_WithBase_Works()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        Assert.Equal(0, Run(repo, "create", "1", "x", "--base", Epic, "--config", config).Code);
    }

    [Theory]
    [InlineData("--epic", "42", "--base", "main")]
    [InlineData]
    public void Create_NeedsExactlyOneBase_Exit2(params string[] baseArgs)
    {
        var (repo, config) = Setup();
        using var _ = repo;
        var (code, output, err) = Run(repo, ["create", "1", "x", .. baseArgs, "--config", config]);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Contains("--epic", err);
    }

    [Fact]
    public void Create_UnknownOrClosedEpic_Exit3()
    {
        var (repo, config) = Setup(EpicStates.Closed);
        using var _ = repo;
        Assert.Equal(ExitCodes.BadInput, Run(repo, "create", "1", "x", "--epic", "7", "--config", config).Code);
        var (code, _, err) = Run(repo, "create", "1", "x", "--epic", "42", "--config", config);
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic '42' is closed", err);
    }

    [Fact]
    public void List_AndPruneDryRun_PrintOneJsonLine()
    {
        var (repo, config) = Setup();
        using var _ = repo;
        Run(repo, "create", "1", "x", "--epic", "42", "--config", config);
        var list = SingleJsonLine(Run(repo, "list", "--epic", "42", "--config", config).Out);
        Assert.Equal(1, list.GetProperty("worktrees").GetArrayLength());
        var (code, output, _) = Run(repo, "prune", "--dry-run", "--config", config);
        Assert.Equal(0, code);
        var report = SingleJsonLine(output);
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.Equal("keep", report.GetProperty("items")[0].GetProperty("action").GetString());
    }

    [Fact]
    public void Prune_FailedItem_PrintsReportAndExits4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (repo, config) = Setup();
        using var _ = repo;
        var created = SingleJsonLine(Run(repo, "create", "1", "x", "--epic", "42", "--config", config).Out);
        var path = created.GetProperty("path").GetString()!;
        using var held = new FileStream(Path.Combine(path, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);
        var (code, output, err) = Run(repo, "prune", "--force", "--config", config);
        Assert.Equal(ExitCodes.Environment, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("failed").GetInt32());
        Assert.StartsWith("error: 1 worktree(s) could not be pruned", err.Split('\n').Last(l => l.Length > 0));
    }

    [Theory]
    [InlineData("create", "1")]
    [InlineData("nope")]
    [InlineData("prune", "--bogus")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        var (repo, _) = Setup();
        using var __ = repo;
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void OutsideRepo_Exits3()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.BadInput, WorktreeProgram.Run(["list"], new StringWriter(), new StringWriter(), dir.Dir));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~WorktreeCliTests`
Expected: FAIL to compile (`Program.Run` not defined).

- [ ] **Step 3: Implement the CLI** (`src/Swarm.Worktree.Cli/Program.cs`)

```csharp
using System.CommandLine;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Worktree.Cli;

/// <summary>The <c>worktree</c> tool: per-task git worktrees branched from an epic branch.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line per command.</param>
    /// <param name="stderr">Receives warnings and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();
        var epic = new Option<string?>("--epic") { Description = "Epic id (its branch is read from <state>/epics/<id>.json)" };
        var baseBranch = new Option<string?>("--base") { Description = "Base branch name, instead of --epic" };

        var ticket = new Argument<string>("ticket") { Description = "Ticket id, e.g. 9933" };
        var slug = new Argument<string>("slug") { Description = "Lowercase hyphenated slug, e.g. login-form" };
        var kind = new Option<string?>("--kind") { Description = "Value for {kind} in worktree.branchTemplate (e.g. feature, bugfix)" };
        var create = new Command("create", "Create a task worktree on a new branch from the epic branch; prints one JSON line") { ticket, slug, epic, baseBranch, kind };
        common.AddTo(create);
        create.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var from = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: true, allowClosed: false)!;
            var result = manager.Create(new CreateRequest(p.GetValue(ticket)!, p.GetValue(slug)!, from, p.GetValue(kind)));
            var progress = new Progress(stderr, ctx.Verbosity);
            foreach (var w in result.Warnings)
            {
                progress.Warn(w);
            }

            stdout.WriteLine(SwarmJson.Line(result));
            return ExitCodes.Ok;
        });

        var all = new Option<bool>("--all") { Description = "Also list worktrees this tool did not create" };
        var list = new Command("list", "List task worktrees with dirty/empty/merged/locked state; prints one JSON line") { epic, baseBranch, all };
        common.AddTo(list);
        list.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var filter = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: false, allowClosed: true);
            stdout.WriteLine(SwarmJson.Line(new WorktreeListResult(SwarmJson.SchemaVersion, manager.Root, manager.List(filter, p.GetValue(all)))));
            return ExitCodes.Ok;
        });

        var dryRun = new Option<bool>("--dry-run") { Description = "Report what would be removed; change nothing" };
        var force = new Option<bool>("--force") { Description = "Also remove unmerged, dirty and empty worktrees (never locked ones)" };
        var prune = new Command("prune", "Remove merged task worktrees and their branches; prints one JSON line") { epic, baseBranch, dryRun, force };
        common.AddTo(prune);
        prune.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var filter = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: false, allowClosed: true);
            var report = new Pruner(manager).Prune(filter, p.GetValue(dryRun), p.GetValue(force));
            stdout.WriteLine(SwarmJson.Line(report));
            if (report.Failed == 0)
            {
                return ExitCodes.Ok;
            }

            stderr.WriteLine(ToolException.Format($"{report.Failed} worktree(s) could not be pruned", "see items[].error; a process may hold files there"));
            return ExitCodes.Environment;
        });

        var root = new RootCommand("worktree - per-task git worktrees from an epic branch") { create, list, prune };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}

/// <summary>Resolves <c>--epic</c> / <c>--base</c> to a branch name.</summary>
internal static class BaseOption
{
    /// <summary>Resolves the base branch.</summary>
    /// <param name="p">Parse result.</param>
    /// <param name="epic">The <c>--epic</c> option.</param>
    /// <param name="baseBranch">The <c>--base</c> option.</param>
    /// <param name="state">State layout (epic records).</param>
    /// <param name="required">Whether one of the two must be given.</param>
    /// <param name="allowClosed">Whether a closed epic is acceptable.</param>
    /// <returns>The branch, or null when neither was given and none is required.</returns>
    /// <exception cref="ToolException">Both or (when required) neither given (2); unknown or closed epic (3).</exception>
    public static string? Resolve(ParseResult p, Option<string?> epic, Option<string?> baseBranch, StateLayout state, bool required, bool allowClosed)
    {
        var (id, name) = (p.GetValue(epic), p.GetValue(baseBranch));
        if (id is not null && name is not null)
        {
            throw new ToolException(ExitCodes.Usage, "pass only one of --epic or --base");
        }

        if (id is null)
        {
            return name ?? (required ? throw new ToolException(ExitCodes.Usage, "pass --epic <id> or --base <branch>") : null);
        }

        var record = new EpicStore(state).Get(id);
        return allowClosed || record.State == EpicStates.Open
            ? record.Branch
            : throw new ToolException(ExitCodes.BadInput, $"epic '{id}' is closed", "open a new epic for new work");
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~WorktreeCliTests`
Expected: all PASS (11 tests).

- [ ] **Step 5: Verify the package builds**

Run: `cd <repo-root> && dotnet pack src/Swarm.Worktree.Cli -c Release -o .docs/feed -warnaserror`
Expected: `Successfully created package '...Swarm.Worktree.0.1.0.nupkg'`, 0 warnings. The `dnx` smoke test for both tools is in Task 10.

- [ ] **Step 6: Write the worktree half of `docs/worktree-epic-tools.md`**. Use front-matter `created: <today>`, `updated: <today>`, `status: current`, and state only behaviour the tests pin:
  1. **Requirement and NOT REAL warning:** `.NET 10+`; `Swarm.Worktree` and `Swarm.Epic` are placeholder ids that are unclaimed on nuget.org (dependency-confusion risk), so run them only with `--add-source <your feed>`; link `dnx-invocation-notes.md`; both tools share `.swarm/batch.json` with testgate, batch and squash (link `batch-tools.md#configuration` and `squash-tool.md#configuration`), and every tool's loader rejects unknown keys, so a config that uses `worktree`/`epicTool` needs `Swarm.TestGate` >= 0.1.2, `Swarm.Batch` >= 0.2.1 and `Swarm.Squash` >= 0.1.1; re-pack every tool after a config schema change.
  2. **Branch naming** (`## Branch naming`): the `worktree` and `epicTool` sections (C3), why the second is not called `epic`, the placeholders, the example-org example from C3 verbatim, and the rule that `feat/`, `fix/` and other short forms are rejected because example-org pipelines match only `feature/` and `bugfix/`. Include every error message from `BranchTemplateTests` and `BranchSectionConfigTests`.
  3. **worktree**: the usage lines from this task's Interfaces; the path `<worktreeRoot>/t-<ticket>`; why the tool, not `isolation: worktree`, creates task worktrees (built-in isolation branches from the default branch, workflow section 2); idempotent create; branch metadata in git config (C4); the MAX_PATH warning and 200-char guard.
  4. **Prune rules** (`## Prune rules`): the decision table from `Pruner.Decide` in order; the merged checks (C5, including the git 2.38 note); `--dry-run`; `--force`; that locked worktrees are never removed; the repository-wide effect of `git worktree prune`; per-item failures and exit 4, including the C6 observation that a removal failed on a held file leaves the directory behind unregistered (pinned by `PrunerTests.HeldFile_FailsItemKeepsBranchContinues`) and what the user does about it; that the merged checks see `squash run` landings only through ancestry or content (C5).
  5. **Exit codes** for `worktree` (C10).

- [ ] **Step 7: Commit**

```bash
git add src tests docs/worktree-epic-tools.md
git commit -m "Add worktree CLI (create, list, prune) and its documentation" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 7: Epic open (branch, record, batch epic id)

**Files:**
- Create: `src/Swarm.Delivery/EpicOpener.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/EpicOpenerTests.cs`

**Interfaces:**
- Consumes: `RepoPaths`, `GitRunner` (`HeadsRef`, `RefExists`, `RevParse`), `SafeName`, `ToolException` (`Swarm.Git`); `StatePaths.Resolve`, `StateLayout`, `SwarmConfig.EpicBranchTemplate/BaseBranch/Epic`, `SwarmConfig.EpicBranch` (`[JsonIgnore]`, `EpicBranchTemplate.Replace("{epic}", Epic)`), `SwarmJson` (`Swarm.RunState`); `BranchTemplate.Render`, `SwarmConfig.EpicTool` (Task 1); `EpicStore`, `EpicRecord`, `EpicStates` (Task 2). `batch` and `squash run` require the epic branch to exist and to be checked out nowhere (`RepoChecks.EnsureEpic`, exit 3), which is why `open` never checks it out.
- Produces (namespace `Swarm.Delivery`):
  - `static class EpicNaming { static string? BatchEpicId(SwarmConfig config, string epicBranch); }`. This returns the safe-name value X for which `config with { Epic = X }` gives `EpicBranch == epicBranch`, or null.
  - `sealed record EpicOpenResult(int SchemaVersion, bool Created, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, string? BatchEpic, IReadOnlyList<string> Warnings)` (stdout of `epic open`).
  - `sealed class EpicOpener(RepoPaths repo, SwarmConfig config) { EpicOpenResult Open(string id, string slug, string? from, string? kind); }`. Checks, in this order, so that failures create nothing: render and prefix (2), `check-ref-format` (2); the existing record (same branch and open with the branch present: `Created = false`; anything else: 3); the branch already exists (3); the base exists (3). Then `git branch <branch> <base sha>` (no checkout) and save the record.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Delivery/EpicOpenerTests.cs`)

```csharp
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicOpenerTests
{
    static EpicOpener Opener(TempRepo repo, Func<SwarmConfig, SwarmConfig>? tweak = null)
    {
        var config = TestConfig.For(repo);
        return new EpicOpener(RepoLocator.Locate(repo.Root), tweak?.Invoke(config) ?? config);
    }

    static SwarmConfig CiNaming(SwarmConfig c) => c with
    {
        EpicTool = new EpicSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
    };

    [Fact]
    public void Open_CreatesBranchAndRecordWithoutCheckout()
    {
        using var repo = TempRepo.Create();
        var r = Opener(repo).Open("42", "auth", null, null);
        Assert.True(r.Created);
        Assert.Equal(("epic/42-auth", "main", repo.Sha("main")), (r.Branch, r.BaseBranch, r.BaseCommit));
        Assert.Equal(repo.Sha("main"), repo.Sha("epic/42-auth"));
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        var record = new EpicStore(new StateLayout(repo.StateDir)).Get("42");
        Assert.Equal((EpicStates.Open, "epic/42-auth"), (record.State, record.Branch));
    }

    [Fact]
    public void Open_IsIdempotent()
    {
        using var repo = TempRepo.Create();
        Opener(repo).Open("42", "auth", null, null);
        Assert.False(Opener(repo).Open("42", "auth", null, null).Created);
    }

    [Fact]
    public void SameIdOtherSlug_IsBadInput()
    {
        using var repo = TempRepo.Create();
        Opener(repo).Open("42", "auth", null, null);
        var e = Assert.Throws<ToolException>(() => Opener(repo).Open("42", "login", null, null));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("epic '42' already exists as 'epic/42-auth'", e.Message);
    }

    [Fact]
    public void ExistingBranchWithoutRecord_IsBadInput()
    {
        using var repo = TempRepo.Create();
        repo.Epic(name: "epic/42-auth");
        Assert.Contains("already exists", Assert.Throws<ToolException>(() => Opener(repo).Open("42", "auth", null, null)).Message);
    }

    [Fact]
    public void From_MustExist()
    {
        using var repo = TempRepo.Create();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => Opener(repo).Open("42", "auth", "dev", null)).ExitCode);
        repo.Git("branch", "dev");
        Assert.Equal("dev", Opener(repo).Open("42", "auth", "dev", null).BaseBranch);
    }

    [Fact]
    public void CiTemplate_OpensFeatureBranch()
    {
        using var repo = TempRepo.Create();
        Assert.Equal("feature/9933-login", Opener(repo, CiNaming).Open("9933", "login", null, null).Branch);
    }

    [Fact]
    public void FixKind_CreatesNothing()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => Opener(repo, CiNaming).Open("9933", "login", null, "fix"));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Empty(repo.Git("branch", "--list", "fix/*"));
        Assert.Null(new EpicStore(new StateLayout(repo.StateDir)).Find("9933"));
    }

    [Theory]
    [InlineData("epic/{epic}", "epic/42-auth", "42-auth")]
    [InlineData("feature/{epic}", "feature/9933-login", "9933-login")]
    [InlineData("epic/{epic}", "feature/9933-login", null)]
    [InlineData("epic/{epic}/int", "epic/42/int", "42")]
    [InlineData("epic/{epic}", "epic/a/b", null)]
    public void BatchEpicId_InvertsTheBatchTemplate(string template, string branch, string? expected)
    {
        using var repo = TempRepo.Create();
        Assert.Equal(expected, EpicNaming.BatchEpicId(TestConfig.For(repo) with { EpicBranchTemplate = template }, branch));
    }

    [Fact]
    public void UnaddressableByBatch_WarnsWithNullBatchEpic()
    {
        using var repo = TempRepo.Create();
        var r = Opener(repo, CiNaming).Open("9933", "login", null, null);
        Assert.Null(r.BatchEpic);
        Assert.Contains(r.Warnings, w => w.Contains("epicBranchTemplate", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicOpenerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** (`src/Swarm.Delivery/EpicOpener.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Maps epic branches to the batch tool's epic id.</summary>
public static class EpicNaming
{
    /// <summary>Finds the <c>--epic</c> value for which batch's <c>epicBranchTemplate</c> yields this branch.</summary>
    /// <param name="config">Config (its <see cref="SwarmConfig.EpicBranchTemplate"/> is used).</param>
    /// <param name="epicBranch">Epic branch.</param>
    /// <returns>The batch epic id, or null when the template cannot express the branch with a safe name.</returns>
    public static string? BatchEpicId(SwarmConfig config, string epicBranch)
    {
        var t = config.EpicBranchTemplate;
        var at = t.IndexOf("{epic}", StringComparison.Ordinal);
        var (prefix, suffix) = (t[..at], t[(at + "{epic}".Length)..]);
        if (epicBranch.Length <= prefix.Length + suffix.Length
            || !epicBranch.StartsWith(prefix, StringComparison.Ordinal)
            || !epicBranch.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = epicBranch[prefix.Length..^suffix.Length];
        return SafeName.IsValid(id) && (config with { Epic = id }).EpicBranch == epicBranch ? id : null;
    }
}

/// <summary>stdout of <c>epic open</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Created">False when the epic already existed (idempotent repeat).</param>
/// <param name="Id">Epic id.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="BaseBranch">Branch it was created from.</param>
/// <param name="BaseCommit">Commit it was created at.</param>
/// <param name="BatchEpic">Value for <c>batch run --epic</c>, or null.</param>
/// <param name="Warnings">One-line warnings.</param>
public sealed record EpicOpenResult(int SchemaVersion, bool Created, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, string? BatchEpic, IReadOnlyList<string> Warnings);

/// <summary>Opens epics: creates the branch (no checkout) and its record.</summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicOpener(RepoPaths repo, SwarmConfig config)
{
    readonly GitRunner git = new(repo.MainWorktreeRoot);

    /// <summary>Opens an epic.</summary>
    /// <param name="id">Epic id.</param>
    /// <param name="slug">Slug.</param>
    /// <param name="from">Base branch, or null for config <c>baseBranch</c>.</param>
    /// <param name="kind">Value for <c>{kind}</c>, or null.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">Naming or path (2); conflicting epic, existing branch or missing base (3).</exception>
    public EpicOpenResult Open(string id, string slug, string? from, string? kind)
    {
        var branch = BranchTemplate.Render(config.EpicTool, id, slug, kind);
        if (git.Try("check-ref-format", "--branch", branch).ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Usage, $"branch name '{branch}' is not valid for git");
        }

        var store = new EpicStore(new StateLayout(StatePaths.Resolve(repo, config.StateDir)));
        var exists = git.RefExists(GitRunner.HeadsRef(branch));
        if (store.Find(id) is { } record)
        {
            return record.Branch == branch && record.State == EpicStates.Open && exists
                ? Result(record, created: false)
                : throw new ToolException(ExitCodes.BadInput, $"epic '{id}' already exists as '{record.Branch}' ({record.State})", "use another id");
        }

        if (exists)
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' already exists", "use another slug, or delete the branch");
        }

        var baseBranch = from ?? config.BaseBranch;
        if (!git.RefExists(GitRunner.HeadsRef(baseBranch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"base branch '{baseBranch}' not found");
        }

        var baseCommit = git.RevParse(GitRunner.HeadsRef(baseBranch));
        git.Run("branch", branch, baseCommit);
        var created = new EpicRecord(SwarmJson.SchemaVersion, id, slug, branch, baseBranch, baseCommit, DateTime.UtcNow, EpicStates.Open, null, null, null);
        store.Save(created);
        return Result(created, created: true);
    }

    EpicOpenResult Result(EpicRecord r, bool created)
    {
        var batchEpic = EpicNaming.BatchEpicId(config, r.Branch);
        IReadOnlyList<string> warnings = batchEpic is null
            ? [$"batch cannot address '{r.Branch}' with epicBranchTemplate '{config.EpicBranchTemplate}'; change epicBranchTemplate (e.g. to the epic branch prefix + {{epic}})"]
            : [];
        return new EpicOpenResult(SwarmJson.SchemaVersion, created, r.Id, r.Slug, r.Branch, r.BaseBranch, r.BaseCommit, batchEpic, warnings);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicOpenerTests`
Expected: all PASS (13 tests).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add epic open: branch from the active branch, record, batch epic id" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 8: Epic assessor (status and close blockers)

**Files:**
- Create: `src/Swarm.Delivery/EpicAssessor.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/EpicAssessorTests.cs`

**Interfaces:**
- Consumes: `RepoPaths`, `GitRunner` (`HeadsRef`, `RefExists`, `RevParse`, `At`), `ToolException`, `TextLines.OneLine` (`Swarm.Git`); `SlotSemaphore(string lockDir, SlotOptions options).Status() -> IReadOnlyList<SlotHolder(int Slot, LockInfo? Info, double HeartbeatAgeSec, bool Stale, bool HolderAlive)>` (lock files `slot-<k>.lock`; a holder in this process counts as alive), `SlotOptions.From(SwarmConfig)`, `StateLayout.BatchLockDir(epic)` (`<state>/locks/batch-<epic>`; **shared by `batch run` and `squash run`**, each holding slot 0 for the whole run), `ReturnedEntry`, `ReturnStage`, `SwarmJson` (`Swarm.RunState`); `EpicRecord`, `EpicStates`, `WorktreeList` (Task 2); `RunHistory`, `TaskStates`, `TaskOutcome`, `MergeCheck` (Task 3); `WorktreeManager.List/Git/State` (Task 4); `EpicNaming`, `EpicOpener` (Task 7); `RunStateFixture` (Task 3). Tests also use Plan B's `SquashRunner(ToolContext, Progress).Run(new SquashRunRequest(taskId, branch, ticket, runId))` and `ToolContext` (`Swarm.Squashing`, `Swarm.RunState.Cli`).
- Produces (namespace `Swarm.Delivery`):
  - `static class BlockerCodes` with the nine codes of C9 as constants: `BatchRunning`, `RunUnfinished`, `TasksReturned`, `WorktreesUnmerged`, `NothingToMerge`, `ActiveDirty`, `ActiveBehindUpstream`, `EpicClosed`, `BranchMissing`.
  - `sealed record EpicBlocker(string Code, string Detail, bool Waivable)`.
  - `sealed record TaskReturn(string Task, string State, string Branch, string? Final, string? Kind, string RunId, string? Reason)`. `Reason` is the return record's `Reason`, except for a land-stage return without files, whose real reason batch keeps in `GitOutput` (for example the squash lander's `requireTicket` failure is recorded as `kind: conflict`, `stage: land`, `files: []`, reason `land conflict with the epic tip`): then it is `GitOutput`, one-lined.
  - Open tasks: every `TaskOutcome.Blocking` outcome of the epic's runs, minus those whose branch exists and `MergeCheck.LandedVia(git, branch, epic.Branch, history.LandedBranches())` reports landed (a task returned by batch and later landed by `squash run`, which writes no run state; C5).
  - `sealed record EpicStatus(int SchemaVersion, string Id, string Slug, string Branch, string State, string Into, string? Tip, int Ahead, int Behind, string? BatchEpic, int Runs, string? LatestRunId, int? LatestRunExitCode, IReadOnlyList<string> LandedTasks, IReadOnlyList<TaskReturn> OpenTasks, int Worktrees, int WorktreesUnmerged, bool BatchRunning, string? Upstream, IReadOnlyList<EpicBlocker> Blockers, bool ReadyToClose)`.
  - `sealed record EpicStatusList(int SchemaVersion, IReadOnlyList<EpicStatus> Epics)` (stdout of `epic status`).
  - `sealed class EpicAssessor(RepoPaths repo, SwarmConfig config) { EpicStatus Assess(EpicRecord epic, string? into = null); }`. `into` defaults to `epic.BaseBranch`; a missing `into` branch gives `ToolException(BadInput)`. `ReadyToClose` is true when there are no blockers. The tool never fetches.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Delivery/EpicAssessorTests.cs`)

```csharp
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

        public EpicStatus Assess() => new EpicAssessor(Paths, Config).Assess(new EpicStore(new StateLayout(Repo.StateDir)).Get("42"));

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
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicAssessorTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** (`src/Swarm.Delivery/EpicAssessor.cs`)

```csharp
using System.Globalization;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Reasons an epic cannot be closed (<see cref="EpicBlocker.Code"/>).</summary>
public static class BlockerCodes
{
    /// <summary>A batch or squash run holds the per-epic lock they share (not waivable).</summary>
    public const string BatchRunning = "batch-running";

    /// <summary>A batch run has no summary: crashed or killed (waivable).</summary>
    public const string RunUnfinished = "run-unfinished";

    /// <summary>Tasks returned or left unprocessed by their latest run (waivable).</summary>
    public const string TasksReturned = "tasks-returned";

    /// <summary>Task worktrees on the epic hold unmerged or uncommitted work (waivable).</summary>
    public const string WorktreesUnmerged = "worktrees-unmerged";

    /// <summary>The epic has no commits beyond the active branch (not waivable).</summary>
    public const string NothingToMerge = "nothing-to-merge";

    /// <summary>The active branch's worktree has tracked changes (not waivable).</summary>
    public const string ActiveDirty = "active-dirty";

    /// <summary>The active branch is behind its last-fetched upstream (not waivable).</summary>
    public const string ActiveBehindUpstream = "active-behind-upstream";

    /// <summary>The epic is already closed (not waivable).</summary>
    public const string EpicClosed = "epic-closed";

    /// <summary>The epic branch does not exist (not waivable).</summary>
    public const string BranchMissing = "branch-missing";
}

/// <summary>One reason an epic cannot be closed.</summary>
/// <param name="Code">A <see cref="BlockerCodes"/> value.</param>
/// <param name="Detail">One-line explanation.</param>
/// <param name="Waivable">True when <c>epic close --force</c> may ignore it.</param>
public sealed record EpicBlocker(string Code, string Detail, bool Waivable);

/// <summary>A task that has not landed.</summary>
/// <param name="Task">Task id.</param>
/// <param name="State">A <see cref="TaskStates"/> value.</param>
/// <param name="Branch">Worker branch, when known.</param>
/// <param name="Final">The return record's final state.</param>
/// <param name="Kind">The return record's kind.</param>
/// <param name="RunId">Run that decided the state.</param>
/// <param name="Reason">The return reason (git's output for a land-stage return without files).</param>
public sealed record TaskReturn(string Task, string State, string Branch, string? Final, string? Kind, string RunId, string? Reason);

/// <summary>Run state and close readiness of one epic.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Id">Epic id.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="State">An <see cref="EpicStates"/> value.</param>
/// <param name="Into">Active branch it would merge into.</param>
/// <param name="Tip">Epic tip, or null when the branch is missing.</param>
/// <param name="Ahead">Epic commits not on <paramref name="Into"/>.</param>
/// <param name="Behind"><paramref name="Into"/> commits not on the epic.</param>
/// <param name="BatchEpic">The batch epic id, or null.</param>
/// <param name="Runs">Batch runs of this epic.</param>
/// <param name="LatestRunId">Newest run.</param>
/// <param name="LatestRunExitCode">Its exit code (null when unfinished).</param>
/// <param name="LandedTasks">Tasks landed across runs.</param>
/// <param name="OpenTasks">Tasks returned or unprocessed.</param>
/// <param name="Worktrees">Managed task worktrees on the epic.</param>
/// <param name="WorktreesUnmerged">Of those, with unmerged or uncommitted work.</param>
/// <param name="BatchRunning">True when a live batch or squash run holds the epic lock.</param>
/// <param name="Upstream">Active branch's upstream (last fetched), or null.</param>
/// <param name="Blockers">Reasons it cannot close now.</param>
/// <param name="ReadyToClose">True when there are no blockers.</param>
public sealed record EpicStatus(
    int SchemaVersion, string Id, string Slug, string Branch, string State, string Into, string? Tip, int Ahead, int Behind, string? BatchEpic,
    int Runs, string? LatestRunId, int? LatestRunExitCode, IReadOnlyList<string> LandedTasks, IReadOnlyList<TaskReturn> OpenTasks,
    int Worktrees, int WorktreesUnmerged, bool BatchRunning, string? Upstream, IReadOnlyList<EpicBlocker> Blockers, bool ReadyToClose);

/// <summary>stdout of <c>epic status</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Epics">One status per epic, by id.</param>
public sealed record EpicStatusList(int SchemaVersion, IReadOnlyList<EpicStatus> Epics);

/// <summary>Computes an epic's status and close blockers (one function for status and close).</summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicAssessor(RepoPaths repo, SwarmConfig config)
{
    /// <summary>Assesses an epic.</summary>
    /// <param name="epic">The epic record.</param>
    /// <param name="into">Target branch, or null for the epic's base branch.</param>
    /// <returns>The status.</returns>
    /// <exception cref="ToolException">The target branch does not exist (exit code 3).</exception>
    public EpicStatus Assess(EpicRecord epic, string? into = null)
    {
        var manager = new WorktreeManager(repo, config);
        var git = manager.Git;
        into ??= epic.BaseBranch;
        if (!git.RefExists(GitRunner.HeadsRef(into)))
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{into}' not found");
        }

        var blockers = new List<EpicBlocker>();
        if (epic.State == EpicStates.Closed)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.EpicClosed, $"epic '{epic.Id}' was merged into '{epic.MergedInto}' at {epic.MergeCommit}", false));
        }

        string? tip = null;
        int ahead = 0, behind = 0;
        if (!git.RefExists(GitRunner.HeadsRef(epic.Branch)))
        {
            blockers.Add(new EpicBlocker(BlockerCodes.BranchMissing, $"epic branch '{epic.Branch}' not found", false));
        }
        else
        {
            tip = git.RevParse(GitRunner.HeadsRef(epic.Branch));
            var counts = git.Run("rev-list", "--left-right", "--count", $"{GitRunner.HeadsRef(into)}...{GitRunner.HeadsRef(epic.Branch)}").Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            (behind, ahead) = (int.Parse(counts[0], CultureInfo.InvariantCulture), int.Parse(counts[1], CultureInfo.InvariantCulture));
            if (ahead == 0 && epic.State == EpicStates.Open)
            {
                blockers.Add(new EpicBlocker(BlockerCodes.NothingToMerge, $"'{epic.Branch}' has no commits that are not on '{into}'", false));
            }
        }

        var batchEpic = EpicNaming.BatchEpicId(config, epic.Branch);
        var running = batchEpic is not null
            && new SlotSemaphore(manager.State.BatchLockDir(batchEpic), SlotOptions.From(config) with { Slots = 1 }).Status().Any(h => !h.Stale && h.HolderAlive);
        if (running)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.BatchRunning, $"a batch or squash run holds epic '{batchEpic}'; wait for it to finish", false));
        }

        var history = RunHistory.Load(manager.State);
        var runs = history.ForEpic(epic.Branch);
        var unfinished = runs.Where(r => !r.Finished).Select(r => r.RunId).ToList();
        if (!running && unfinished.Count > 0)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.RunUnfinished, $"batch run(s) without summary.json (crashed or killed): {string.Join(", ", unfinished)}", true));
        }

        // squash run writes no run state (C5): a task batch returned and a human then landed by hand is found on the epic.
        var ledger = history.LandedBranches();
        bool LandedSince(TaskOutcome o) =>
            tip is not null
            && o.Branch.Length > 0
            && git.RefExists(GitRunner.HeadsRef(o.Branch))
            && MergeCheck.LandedVia(git, o.Branch, epic.Branch, ledger) is not null;

        var outcomes = RunHistory.Outcomes(runs).Values.ToList();
        var open = outcomes
            .Where(o => o.Blocking && !LandedSince(o))
            .Select(o => new TaskReturn(o.Task, o.State, o.Branch, o.LastReturn?.Final, o.LastReturn?.Kind, o.RunId, ReasonOf(o.LastReturn)))
            .ToList();
        if (open.Count > 0)
        {
            var list = string.Join(", ", open.Select(t => $"{t.Task} ({t.Final ?? t.State})"));
            blockers.Add(new EpicBlocker(BlockerCodes.TasksReturned, $"{open.Count} task(s) not landed: {list}", true));
        }

        var worktrees = manager.List(epic.Branch);
        var unmerged = worktrees.Where(w => w.MergedVia is null && (w.AheadOfBase > 0 || w.Dirty)).ToList();
        if (unmerged.Count > 0)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.WorktreesUnmerged, "unmerged work in task worktrees: " + string.Join(", ", unmerged.Select(w => $"{w.Ticket} ({w.Branch})")), true));
        }

        if (WorktreeList.CheckedOut(git, into) is { Prunable: false } active
            && Directory.Exists(active.Path)
            && git.At(active.Path).Run("status", "--porcelain", "--untracked-files=no").Length > 0)
        {
            blockers.Add(new EpicBlocker(BlockerCodes.ActiveDirty, $"'{into}' is checked out at '{active.Path}' with uncommitted changes to tracked files", false));
        }

        var up = git.Try("rev-parse", "--abbrev-ref", "--symbolic-full-name", into + "@{upstream}");
        var upstream = up.ExitCode == 0 ? up.StdOut.Trim() : null;
        if (upstream is not null)
        {
            var missing = int.Parse(git.Run("rev-list", "--count", $"{GitRunner.HeadsRef(into)}..{upstream}"), CultureInfo.InvariantCulture);
            if (missing > 0)
            {
                blockers.Add(new EpicBlocker(BlockerCodes.ActiveBehindUpstream, $"'{into}' is {missing} commit(s) behind '{upstream}' as last fetched; pull first", false));
            }
        }

        var latest = runs.LastOrDefault();
        return new EpicStatus(
            SwarmJson.SchemaVersion, epic.Id, epic.Slug, epic.Branch, epic.State, into, tip, ahead, behind, batchEpic,
            runs.Count, latest?.RunId, latest?.Summary?.ExitCode,
            outcomes.Where(o => o.State == TaskStates.Landed).Select(o => o.Task).Order(StringComparer.Ordinal).ToList(), open,
            worktrees.Count, unmerged.Count, running, upstream, blockers, blockers.Count == 0);
    }

    // batch records a land failure as "land conflict with the epic tip" with no files and keeps the real reason (for
    // example the squash lander's requireTicket failure) in GitOutput.
    static string? ReasonOf(ReturnedEntry? r) =>
        r is { Stage: ReturnStage.Land, Files.Count: 0, GitOutput.Length: > 0 } ? TextLines.OneLine(r.GitOutput) : r?.Reason;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicAssessorTests`
Expected: all PASS (15 tests).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add epic assessor: run state, open tasks, worktrees and close blockers" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 9: Epic close (trailer log, merge message, `--no-ff` merge, active-branch update)

**Files:**
- Create: `src/Swarm.Delivery/TrailerLog.cs`, `src/Swarm.Delivery/MergeMessage.cs`, `src/Swarm.Delivery/EpicCloser.cs`
- Test: `tests/Swarm.Tools.Tests/Delivery/MergeMessageTests.cs`, `tests/Swarm.Tools.Tests/Delivery/EpicCloserTests.cs`

**Interfaces:**
- Consumes: `GitRunner` (`HeadsRef`, `At`, `Try`, `Run`, `Lines`, `RevParse`), `ToolException`, `TextLines`, `FileTree.DeleteTree` (`Swarm.Git`); `StatePaths.Guard`, `SwarmJson` (`Swarm.RunState`); `EpicStore`, `EpicRecord`, `EpicStates`, `WorktreeList` (Task 2); `WorktreeManager.Root/Git/State` (Task 4); `EpicNaming.BatchEpicId`, `EpicOpener` (Task 7); `EpicAssessor`, `EpicStatus.BatchEpic`, `EpicBlocker`, `BlockerCodes` (Task 8); `RunStateFixture` (Task 3). Tests also use Plan B's message builder `SquashMessage.Build(SquashConfig, SquashGroup, MessageContext)` with `SquashGroup(string Ticket, IReadOnlyList<LandTask> Tasks, IReadOnlyList<string?> SourceTips, IReadOnlyList<SourceCommit> Commits)`, `SourceCommit(Sha, AuthorName, AuthorEmail, Subject)`, `MessageContext(string Epic, int Batch, string RunId)` (`Swarm.Squashing`) and `LandTask(Id, Branch, DependsOn)` (`Swarm.Batching`).
- What Plan B writes (source: `src/Swarm.Squashing/SquashMessage.cs`; example in `docs/squash-tool.md#what-lands`): subject from `squash.subjectTemplate`, default `{ticket}: {title}` (so the subject usually **starts with the ticket**); an optional `Squashed commits:` list; then one final trailer paragraph, LF, values one-lined, in this order: `Ticket`, `Epic` (the **batch epic id**, `config.Epic`, e.g. `42-auth` for `epic/42-auth` under `epicBranchTemplate` `epic/{epic}`, never `EpicRecord.Id`), `Batch` (per run, restarting at 1 in every batch run; `0` for a manual `squash run`), `Swarm-Run` (run id), then `Task` (+ `Source-Commit`) per task, then `Co-authored-by`. The fast-forward lander stamps nothing (its per-task merge commits are skipped by `--no-merges`; the task's own commits carry whatever the worker wrote).
- Produces (namespace `Swarm.Delivery`):
  - `sealed record TrailerCommit(string Sha, string Subject, IReadOnlyList<string> Tickets, IReadOnlyList<string> Epics, IReadOnlyList<string> Batches, IReadOnlyList<string> Runs)`; `static class TrailerLog { static IReadOnlyList<TrailerCommit> Read(GitRunner git, string fromRef, string toRef); static IReadOnlyList<TrailerCommit> Parse(string logOutput); }` (non-merge commits in `from..to`, oldest first; trailer keys `Ticket`, `Epic`, `Batch` and `Swarm-Run` are matched case-insensitively).
  - `static class MergeMessage { const string ManualBatch = "0"; static string Build(EpicRecord epic, string? batchEpic, string into, IReadOnlyList<TrailerCommit> commits); static string Title(TrailerCommit commit); static IReadOnlyList<string> Tickets(IReadOnlyList<TrailerCommit> commits); }` (LF, no trailing newline). A commit belongs to the epic when its `Epic:` value is `epic.Id` **or** `batchEpic` (the value `EpicNaming.BatchEpicId` gives for the epic branch; `EpicCloser` passes `EpicStatus.BatchEpic`). `Title` drops a leading ticket followed by `:`, space or `-` from the subject (the rule of Plan B's `SquashMessage.Title`, which `Swarm.Delivery` cannot reference, C1). Batch `0` renders as `manual`; because batch numbers restart in every run, the header counts distinct `Swarm-Run:` values (`Runs: <n>`) instead of listing batch numbers.
  - `static class CloseResults { const string Merged = "merged"; const string Blocked = "blocked"; const string Conflict = "conflict"; const string DryRun = "dry-run"; }`
  - `sealed record CloseOptions(string? Into = null, bool Force = false, bool DryRun = false, bool DeleteBranch = false)`.
  - `sealed record EpicCloseResult(int SchemaVersion, string Result, string Id, string Branch, string Into, string? MergeCommit, IReadOnlyList<string> Tickets, IReadOnlyList<EpicBlocker> Blockers, IReadOnlyList<EpicBlocker> Waived, IReadOnlyList<string> ConflictFiles, string Message, bool BranchDeleted)` (stdout of `epic close`; `Blockers` are the ones not waived).
  - `sealed class EpicCloser(RepoPaths repo, SwarmConfig config) { EpicCloseResult Close(string id, CloseOptions options); }`. An unknown epic, an already closed epic or a missing target gives 3; the target branch moving during the close (`'<into>' moved during close`), a target ref that cannot be updated although it did not move (`could not move '<into>': <git's reason>`, with a stale-lock hint, as Plan B's `EpicRef.Move` does), or a failed fast-forward, gives 4. Blocked, conflict and dry-run outcomes are results, not exceptions.

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/Delivery/MergeMessageTests.cs`:

```csharp
using Swarm.Batching;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class MergeMessageTests
{
    static readonly EpicRecord Epic = new(1, "42", "auth", "epic/42-auth", "main", new string('0', 40), DateTime.UtcNow, EpicStates.Open, null, null, null);

    [Fact]
    public void Build_ListsTicketsRunsUntrackedAndForeign()
    {
        var commits = new TrailerCommit[]
        {
            new(new string('a', 40), "9933: Login form", ["9933"], ["42-auth"], ["1"], ["run-a"]),
            new(new string('b', 40), "9934: Token refresh", ["9934"], ["42-auth"], ["2"], ["run-a"]),
            new(new string('c', 40), "batch: merge T9 (task/T9)", [], [], [], []),
            new(new string('e', 40), "9935 - Hotfix", ["9935"], ["42-auth"], ["0"], ["squash-20261004-090000-000-42-auth"]),
            new(new string('f', 40), "Older stamp", ["9936"], ["42"], ["1"], ["run-0"]),
            new(new string('d', 40), "Stray", ["9999"], ["7"], ["2"], ["run-b"]),
        };
        const string expected = """
            Merge epic 42-auth (epic/42-auth) into main

            Epic: 42 (batch epic 42-auth)
            Tickets: 9933, 9934, 9935, 9936, 9999
            Runs: 4

            - 9933 (batch 1): Login form
            - 9934 (batch 2): Token refresh
            - 9935 (manual): Hotfix
            - 9936 (batch 1): Older stamp
            - 9999 (batch 2): Stray

            Commits without a Ticket: trailer: 1
            Commits naming another epic: ddddddd (Epic: 7)
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), MergeMessage.Build(Epic, "42-auth", "main", commits));
        Assert.Equal(new[] { "9933", "9934", "9935", "9936", "9999" }, MergeMessage.Tickets(commits));
    }

    [Theory]
    [InlineData("9933: Login form", "Login form")]
    [InlineData("9933 - Login form", "Login form")]
    [InlineData("9933", "9933")]
    [InlineData("99330 bigger number", "99330 bigger number")]
    [InlineData("Login form", "Login form")]
    public void Title_DropsALeadingTicketLikeTheSquashLander(string subject, string title) =>
        Assert.Equal(title, MergeMessage.Title(new TrailerCommit(new string('a', 40), subject, ["9933"], [], [], [])));

    [Fact]
    public void Build_NoTrailers_SaysSo()
    {
        var text = MergeMessage.Build(Epic, null, "main", [new TrailerCommit(new string('a', 40), "x", [], [], [], [])]);
        Assert.StartsWith("Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42\n", text);
        Assert.Contains("Tickets: none (no Ticket: trailers found)", text);
        Assert.DoesNotContain("Runs:", text);
        Assert.DoesNotContain('\r', text);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void Read_ParsesTrailersFromGitOldestFirst()
    {
        using var repo = TempRepo.Create();
        repo.Git("checkout", "-q", "-b", "epic/42-auth");
        repo.Commit("Login form\n\nBody text.\n\nTicket: 9933\nepic: 42-auth\nBatch: 1\nswarm-run: run-1", ("a.txt", "a\n"));
        repo.Commit("No trailers", ("b.txt", "b\n"));
        repo.Git("checkout", "-q", "main");
        var commits = TrailerLog.Read(new GitRunner(repo.Root), "refs/heads/main", "refs/heads/epic/42-auth");
        Assert.Equal(2, commits.Count);
        Assert.Equal(("Login form", "9933", "42-auth", "1", "run-1"), (commits[0].Subject, commits[0].Tickets.Single(), commits[0].Epics.Single(), commits[0].Batches.Single(), commits[0].Runs.Single()));
        Assert.Empty(commits[1].Tickets);
        Assert.Equal(repo.Sha("epic/42-auth"), commits[1].Sha);
    }

    [Fact]
    public void Read_ParsesTheTrailerBlockPlanBWrites()
    {
        // Built by Plan B's own SquashMessage (two tasks, a commit list, Source-Commit and Co-authored-by), so a
        // change to the trailer block breaks this test.
        var group = new SquashGroup(
            "9933",
            [new LandTask("T1", "task/9933-login", []), new LandTask("T2", "task/9933-more", ["T1"])],
            [new string('1', 40), null],
            [new SourceCommit(new string('2', 40), "Ada", "ada@example.invalid", "9933: Login form"), new SourceCommit(new string('3', 40), "Bob", "bob@example.invalid", "fix form")]);
        var message = SquashMessage.Build(new SquashConfig(), group, new MessageContext("42-auth", 3, "run-1"));
        using var repo = TempRepo.Create();
        repo.Git("checkout", "-q", "-b", "epic/42-auth");
        repo.Commit(message, ("a.txt", "a\n"));
        repo.Git("checkout", "-q", "main");
        var c = Assert.Single(TrailerLog.Read(new GitRunner(repo.Root), "refs/heads/main", "refs/heads/epic/42-auth"));
        Assert.Equal(("9933: Login form", "9933", "42-auth", "3", "run-1"), (c.Subject, c.Tickets.Single(), c.Epics.Single(), c.Batches.Single(), c.Runs.Single()));
        var text = MergeMessage.Build(Epic, "42-auth", "main", [c]);
        Assert.Contains("\n- 9933 (batch 3): Login form", text);
        Assert.DoesNotContain("another epic", text);
    }
}
```

`tests/Swarm.Tools.Tests/Delivery/EpicCloserTests.cs`:

```csharp
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

        public EpicCloseResult Close(CloseOptions? options = null) => new EpicCloser(Paths, Config).Close("42", options ?? new CloseOptions());

        public void OnEpic(string message, params (string Path, string Content)[] files)
        {
            var back = Repo.Git("rev-parse", "--abbrev-ref", "HEAD");
            Repo.Git("checkout", "-q", Epic);
            Repo.Commit(message, files);
            Repo.Git("checkout", "-q", back);
        }

        public void ReturnedTask() =>
            RunStateFixture.WriteRun(Repo.StateDir, "r1", DateTime.UtcNow, Epic, [], [RunStateFixture.Returned("T3", "task/T3", FinalState.ReturnedRed)]);

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
        Assert.False(Directory.Exists(Path.Combine(f.Repo.WorktreeRoot, "close-42")));
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
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
        Assert.Equal(EpicStates.Open, f.Store.Get("42").State);
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
        Assert.False(Directory.Exists(Path.Combine(f.Repo.WorktreeRoot, "close-42")));
        Assert.DoesNotContain(WorktreeList.Read(new GitRunner(f.Repo.Root)), w => w.Path.EndsWith("close-42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRun_ChangesNothingAndReturnsMessage()
    {
        using var f = new Fixture();
        var r = f.Close(new CloseOptions(DryRun: true));
        Assert.Equal(CloseResults.DryRun, r.Result);
        Assert.Contains("- 9934 (batch 2): Token refresh", r.Message);
        Assert.Equal(f.MainBefore, f.Repo.Sha("main"));
    }

    [Fact]
    public void AlreadyClosed_IsBadInput()
    {
        using var f = new Fixture();
        f.Close();
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => f.Close()).ExitCode);
    }

    [Fact]
    public void DeleteBranch_RemovesMergedEpicBranch()
    {
        using var f = new Fixture();
        Assert.True(f.Close(new CloseOptions(DeleteBranch: true)).BranchDeleted);
        Assert.Empty(f.Repo.Git("branch", "--list", Epic));
    }

    [Fact]
    public void StaleCloseWorktree_IsReplaced()
    {
        using var f = new Fixture();
        var stale = Path.Combine(f.Repo.WorktreeRoot, "close-42");
        f.Repo.Git("worktree", "add", "-q", "--detach", stale, "main");
        File.WriteAllText(Path.Combine(stale, "junk.txt"), "x");
        Assert.Equal(CloseResults.Merged, f.Close().Result);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~MergeMessageTests|FullyQualifiedName~EpicCloserTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `TrailerLog`** (`src/Swarm.Delivery/TrailerLog.cs`)

```csharp
using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>A commit and its swarm trailers.</summary>
/// <param name="Sha">Commit sha.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Tickets"><c>Ticket:</c> values.</param>
/// <param name="Epics"><c>Epic:</c> values.</param>
/// <param name="Batches"><c>Batch:</c> values (per run; <c>0</c> = manual <c>squash run</c>).</param>
/// <param name="Runs"><c>Swarm-Run:</c> values.</param>
public sealed record TrailerCommit(string Sha, string Subject, IReadOnlyList<string> Tickets, IReadOnlyList<string> Epics, IReadOnlyList<string> Batches, IReadOnlyList<string> Runs);

/// <summary>Reads <c>Ticket:</c>/<c>Epic:</c>/<c>Batch:</c>/<c>Swarm-Run:</c> trailers from git history.</summary>
public static class TrailerLog
{
    const char Field = '\x1f';
    const char Record = '\x1e';

    /// <summary>Reads non-merge commits in <c>from..to</c>, oldest first.</summary>
    /// <param name="git">Runner in any worktree.</param>
    /// <param name="fromRef">Excluded side (the active branch).</param>
    /// <param name="toRef">Included side (the epic branch).</param>
    /// <returns>The commits.</returns>
    public static IReadOnlyList<TrailerCommit> Read(GitRunner git, string fromRef, string toRef) =>
        Parse(git.Run("log", "--reverse", "--no-merges", "--format=%H%x1f%s%x1f%(trailers:only,unfold)%x1e", $"{fromRef}..{toRef}"));

    /// <summary>Parses the output of <see cref="Read"/>'s log format.</summary>
    /// <param name="logOutput">Records separated by U+001E, fields by U+001F.</param>
    /// <returns>The commits.</returns>
    public static IReadOnlyList<TrailerCommit> Parse(string logOutput)
    {
        var commits = new List<TrailerCommit>();
        foreach (var raw in logOutput.Split(Record))
        {
            // Only CR/LF are trimmed: the field separator must survive (empty trailer blocks end in it).
            var fields = raw.Trim('\r', '\n').Split(Field);
            if (fields.Length < 2 || fields[0].Length == 0)
            {
                continue;
            }

            var trailers = (fields.Length > 2 ? fields[2] : "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(':', 2))
                .Where(kv => kv.Length == 2 && kv[1].Trim().Length > 0)
                .Select(kv => (Key: kv[0].Trim(), Value: kv[1].Trim()))
                .ToList();
            IReadOnlyList<string> Values(string key) =>
                trailers.Where(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).Select(t => t.Value).ToList();
            commits.Add(new TrailerCommit(fields[0].Trim(), fields[1], Values("Ticket"), Values("Epic"), Values("Batch"), Values("Swarm-Run")));
        }

        return commits;
    }
}
```

- [ ] **Step 4: Implement `MergeMessage`** (`src/Swarm.Delivery/MergeMessage.cs`)

```csharp
using System.Text;

namespace Swarm.Delivery;

/// <summary>Builds the <c>--no-ff</c> merge message of an epic close.</summary>
public static class MergeMessage
{
    /// <summary>The <c>Batch:</c> value of a manual <c>squash run</c>.</summary>
    public const string ManualBatch = "0";

    /// <summary>Builds the message.</summary>
    /// <param name="epic">The epic.</param>
    /// <param name="batchEpic">The batch epic id of the epic branch (what the squash lander stamps in <c>Epic:</c>), or null.</param>
    /// <param name="into">Target branch.</param>
    /// <param name="commits">Commits being merged, oldest first.</param>
    /// <returns>LF-separated message without a trailing newline.</returns>
    public static string Build(EpicRecord epic, string? batchEpic, string into, IReadOnlyList<TrailerCommit> commits)
    {
        var sb = new StringBuilder();
        sb.Append($"Merge epic {epic.Id}-{epic.Slug} ({epic.Branch}) into {into}\n\n");
        sb.Append(batchEpic is not null && batchEpic != epic.Id ? $"Epic: {epic.Id} (batch epic {batchEpic})\n" : $"Epic: {epic.Id}\n");
        var tickets = Tickets(commits);
        sb.Append(tickets.Count > 0 ? $"Tickets: {string.Join(", ", tickets)}\n" : "Tickets: none (no Ticket: trailers found)\n");

        // Batch numbers restart in every run, so the header counts runs instead of listing batch numbers.
        var runs = commits.SelectMany(c => c.Runs).Distinct(StringComparer.Ordinal).Count();
        if (runs > 0)
        {
            sb.Append($"Runs: {runs}\n");
        }

        var ticketed = commits.Where(c => c.Tickets.Count > 0).ToList();
        if (ticketed.Count > 0)
        {
            sb.Append('\n');
            foreach (var c in ticketed)
            {
                var batch = c.Batches.Count > 0 ? $" ({string.Join('+', c.Batches.Select(b => b == ManualBatch ? "manual" : "batch " + b))})" : "";
                sb.Append($"- {string.Join('+', c.Tickets)}{batch}: {Title(c)}\n");
            }
        }

        var untracked = commits.Count(c => c.Tickets.Count == 0);
        var foreign = commits.Where(c => c.Epics.Count > 0 && !c.Epics.Any(e => e == epic.Id || e == batchEpic)).ToList();
        if (untracked > 0 || foreign.Count > 0)
        {
            sb.Append('\n');
        }

        if (untracked > 0)
        {
            sb.Append($"Commits without a Ticket: trailer: {untracked}\n");
        }

        if (foreign.Count > 0)
        {
            sb.Append("Commits naming another epic: ").Append(string.Join(", ", foreign.Select(c => $"{c.Sha[..7]} (Epic: {string.Join('+', c.Epics)})"))).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The subject without a leading ticket followed by ':', space or '-' (the squash lander's subject is <c>{ticket}: {title}</c>).</summary>
    /// <param name="commit">The commit.</param>
    /// <returns>The title; the whole subject when nothing would be left.</returns>
    public static string Title(TrailerCommit commit)
    {
        foreach (var ticket in commit.Tickets)
        {
            if (!commit.Subject.StartsWith(ticket, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = commit.Subject[ticket.Length..];
            var title = rest.TrimStart(':', ' ', '-');
            if (rest.Length > 0 && (rest[0] is ':' or ' ' or '-') && title.Length > 0)
            {
                return title;
            }
        }

        return commit.Subject;
    }

    /// <summary>Lists ticket ids in first-seen order.</summary>
    /// <param name="commits">Commits, oldest first.</param>
    /// <returns>Distinct ticket ids.</returns>
    public static IReadOnlyList<string> Tickets(IReadOnlyList<TrailerCommit> commits) =>
        commits.SelectMany(c => c.Tickets).Distinct(StringComparer.Ordinal).ToList();
}
```

- [ ] **Step 5: Implement `EpicCloser`** (`src/Swarm.Delivery/EpicCloser.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Outcome of <c>epic close</c> (<see cref="EpicCloseResult.Result"/>).</summary>
public static class CloseResults
{
    /// <summary>Merged into the active branch (exit 0).</summary>
    public const string Merged = "merged";

    /// <summary>Not merged: blockers remain (exit 1).</summary>
    public const string Blocked = "blocked";

    /// <summary>Not merged: the merge conflicts (exit 1).</summary>
    public const string Conflict = "conflict";

    /// <summary>Checks passed; nothing changed (exit 0).</summary>
    public const string DryRun = "dry-run";
}

/// <summary>Options for <see cref="EpicCloser.Close"/>.</summary>
/// <param name="Into">Target branch, or null for the epic's base branch.</param>
/// <param name="Force">Waive waivable blockers.</param>
/// <param name="DryRun">Check and build the message only.</param>
/// <param name="DeleteBranch">Delete the epic branch after merging (<c>git branch -d</c>).</param>
public sealed record CloseOptions(string? Into = null, bool Force = false, bool DryRun = false, bool DeleteBranch = false);

/// <summary>stdout of <c>epic close</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Result">A <see cref="CloseResults"/> value.</param>
/// <param name="Id">Epic id.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="Into">Target branch.</param>
/// <param name="MergeCommit">The merge commit (merged only).</param>
/// <param name="Tickets">Tickets from <c>Ticket:</c> trailers.</param>
/// <param name="Blockers">Blockers that stopped the close.</param>
/// <param name="Waived">Blockers waived by <c>--force</c>.</param>
/// <param name="ConflictFiles">Conflicting files (conflict only).</param>
/// <param name="Message">The merge message.</param>
/// <param name="BranchDeleted">True when the epic branch was deleted.</param>
public sealed record EpicCloseResult(
    int SchemaVersion, string Result, string Id, string Branch, string Into, string? MergeCommit, IReadOnlyList<string> Tickets,
    IReadOnlyList<EpicBlocker> Blockers, IReadOnlyList<EpicBlocker> Waived, IReadOnlyList<string> ConflictFiles, string Message, bool BranchDeleted);

/// <summary>Closes an epic: <c>merge --no-ff</c> onto the active branch, never squashed.</summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicCloser(RepoPaths repo, SwarmConfig config)
{
    /// <summary>Closes an epic.</summary>
    /// <param name="id">Epic id.</param>
    /// <param name="options">Options.</param>
    /// <returns>The result (blocked and conflict are results, not exceptions).</returns>
    /// <exception cref="ToolException">Unknown or closed epic, missing target (3); target moved or cannot fast-forward (4).</exception>
    public EpicCloseResult Close(string id, CloseOptions options)
    {
        var manager = new WorktreeManager(repo, config);
        var git = manager.Git;
        var store = new EpicStore(manager.State);
        var epic = store.Get(id);
        if (epic.State == EpicStates.Closed)
        {
            throw new ToolException(ExitCodes.BadInput, $"epic '{id}' is already closed (merged into '{epic.MergedInto}' at {epic.MergeCommit})");
        }

        var status = new EpicAssessor(repo, config).Assess(epic, options.Into);
        var into = status.Into;
        IReadOnlyList<EpicBlocker> waived = options.Force ? status.Blockers.Where(b => b.Waivable).ToList() : [];
        var remaining = status.Blockers.Except(waived).ToList();
        IReadOnlyList<TrailerCommit> commits = status.Tip is null ? [] : TrailerLog.Read(git, GitRunner.HeadsRef(into), GitRunner.HeadsRef(epic.Branch));
        var message = MergeMessage.Build(epic, status.BatchEpic, into, commits);
        EpicCloseResult Result(string result, string? merge = null, IReadOnlyList<string>? files = null, bool deleted = false) =>
            new(SwarmJson.SchemaVersion, result, id, epic.Branch, into, merge, MergeMessage.Tickets(commits), remaining, waived, files ?? [], message, deleted);

        if (remaining.Count > 0)
        {
            return Result(CloseResults.Blocked);
        }

        if (options.DryRun)
        {
            return Result(CloseResults.DryRun);
        }

        var intoSha = git.RevParse(GitRunner.HeadsRef(into));
        var path = StatePaths.Guard(Path.Combine(manager.Root, "close-" + id), "close worktree");
        string merge;
        Discard(git, path);
        Directory.CreateDirectory(manager.Root);
        git.Run("worktree", "add", "-q", "--detach", path, intoSha);
        try
        {
            var wt = git.At(path);
            var r = wt.Try("merge", "--no-ff", "--no-edit", "-m", message, status.Tip!);
            if (r.ExitCode != 0)
            {
                var files = wt.Lines("diff", "--name-only", "--diff-filter=U");
                wt.Try("merge", "--abort");
                return Result(CloseResults.Conflict, files: files);
            }

            merge = wt.RevParse("HEAD");
        }
        finally
        {
            Discard(git, path);
        }

        MoveActive(git, into, intoSha, merge, id);
        store.Save(epic with { State = EpicStates.Closed, ClosedUtc = DateTime.UtcNow, MergeCommit = merge, MergedInto = into });
        var deleted = options.DeleteBranch && git.Try("branch", "-d", epic.Branch).ExitCode == 0;
        return Result(CloseResults.Merged, merge, deleted: deleted);
    }

    // Checked out somewhere (normally the user's main worktree): fast-forward it so its files follow.
    // Not checked out: compare-and-swap the ref.
    static void MoveActive(GitRunner git, string into, string intoSha, string merge, string id)
    {
        if (WorktreeList.CheckedOut(git, into) is { } active)
        {
            var at = git.At(active.Path);
            if (!string.Equals(at.RevParse("HEAD"), intoSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ExitCodes.Environment, $"'{into}' moved during close; nothing merged", "re-run epic close");
            }

            var ff = at.Try("merge", "--ff-only", merge);
            if (ff.ExitCode != 0)
            {
                throw new ToolException(ExitCodes.Environment, $"could not fast-forward '{into}' in '{active.Path}': {TextLines.OneLine(ff.StdErr)}", "move files that block the update, then re-run epic close");
            }

            return;
        }

        var r = git.Try("update-ref", "-m", $"epic close {id}", GitRunner.HeadsRef(into), merge, intoSha);
        if (r.ExitCode == 0)
        {
            return;
        }

        // update-ref also fails when the ref cannot be locked (as Plan B's EpicRef.Move learned): only a changed tip
        // means another writer moved it.
        var now = git.Try("rev-parse", "--verify", "-q", GitRunner.HeadsRef(into)).StdOut.Trim();
        throw string.Equals(now, intoSha, StringComparison.OrdinalIgnoreCase)
            ? new ToolException(
                ExitCodes.Environment,
                $"could not move '{into}': {TextLines.OneLine(r.StdErr)}; nothing merged",
                "if no other git process is running, remove the stale lock file git names (refs/heads/<branch>.lock, or reftable/tables.list.lock) and re-run epic close")
            : new ToolException(ExitCodes.Environment, $"'{into}' moved during close; nothing merged", "re-run epic close");
    }

    static void Discard(GitRunner git, string path)
    {
        if (WorktreeList.Read(git).Any(w => WorktreeList.SamePath(w.Path, path)))
        {
            git.Try("worktree", "remove", "--force", path);
        }

        FileTree.DeleteTree(path);
        git.Try("worktree", "prune");
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~MergeMessageTests|FullyQualifiedName~EpicCloserTests"`
Expected: all PASS (20 tests).

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Add epic close: trailer-listed --no-ff merge onto the active branch" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 10: `epic` CLI, `dnx` smoke test of both tools, documentation and decision entry

**Files:**
- Modify: `src/Swarm.Epic.Cli/Program.cs` (replace the placeholder), `docs/worktree-epic-tools.md` (epic half), `docs/decisions.md` (append one section), `docs/dnx-invocation-notes.md` (companion tools), `AGENTS.md`, `README.md` (links)
- Test: `tests/Swarm.Tools.Tests/Cli/EpicCliTests.cs`

**Interfaces:**
- Consumes: `CommonOptions`, `CliHost` (`Swarm.RunState.Cli`), `ConfigOverrides.None`, `Progress`, `SwarmJson` (`Swarm.RunState`), `ToolException.Format` (`Swarm.Git`) (signatures as quoted in Task 6); `JsonOutput.SingleJsonLine` (test support); `EpicStore` (Task 2); `WorktreeManager` (Task 4); `EpicOpener` (Task 7); `EpicAssessor`, `EpicStatusList` (Task 8); `EpicCloser`, `CloseOptions`, `CloseResults` (Task 9).
- Produces:
  - `epic open <id> <slug> [--from <branch>] [--kind <k>] [common]` -> one `EpicOpenResult` line; warnings go to stderr; exit 0.
  - `epic status [<id>] [--into <branch>] [common]` -> one `EpicStatusList` line (one epic, or every epic by id); exit 0; an unknown id gives 3.
  - `epic close <id> [--into <branch>] [--force] [--dry-run] [--delete-branch] [common]` -> one `EpicCloseResult` line. Exit 0 for `merged`/`dry-run`. Exit 1 for `blocked` (stderr `error: epic '<id>' not closed: <first blocker detail> (<n> blocker(s); see blockers)`) and for `conflict` (stderr `error: merging '<branch>' into '<into>' conflicts in <files> (merge '<into>' into the epic and resolve, then close again)`). Exit 2/3/4 with no stdout.
  - `public static int Swarm.Epic.Cli.Program.Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)`.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Cli/EpicCliTests.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using static Swarm.Tools.Tests.Support.JsonOutput;
using EpicProgram = Swarm.Epic.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class EpicCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EpicProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static (TempRepo Repo, string Config) Opened()
    {
        var repo = TempRepo.Create();
        var config = TestConfig.Write(repo, TestConfig.For(repo));
        Assert.Equal(0, Run(repo, "open", "42", "auth", "--config", config).Code);
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Commit("9933: Login\n\nTicket: 9933\nEpic: 42-auth\nBatch: 1\nSwarm-Run: run-1", ("t1.txt", "1\n"));
        repo.Git("checkout", "-q", "main");
        return (repo, config);
    }

    [Fact]
    public void Open_PrintsResultWithBatchEpic()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "open", "42", "auth", "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(("epic/42-auth", "42-auth"), (json.GetProperty("branch").GetString(), json.GetProperty("batchEpic").GetString()));
    }

    [Fact]
    public void Open_DisallowedKind_Exit2NoStdout()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo) with
        {
            EpicTool = new EpicSection { BranchTemplate = "{kind}/{id}-{slug}", DefaultKind = "feature", AllowedPrefixes = ["feature/", "bugfix/"] },
        };
        var (code, output, err) = Run(repo, "open", "9933", "login", "--kind", "fix", "--config", TestConfig.Write(repo, config));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Contains("silently break CI", err);
    }

    [Fact]
    public void Status_AllAndOne()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var all = SingleJsonLine(Run(repo, "status", "--config", config).Out);
        var epic = Assert.Single(all.GetProperty("epics").EnumerateArray());
        Assert.True(epic.GetProperty("readyToClose").GetBoolean());
        Assert.Equal(1, SingleJsonLine(Run(repo, "status", "42", "--config", config).Out).GetProperty("epics").GetArrayLength());
        Assert.Equal(ExitCodes.BadInput, Run(repo, "status", "7", "--config", config).Code);
    }

    [Fact]
    public void Close_Merged_Exit0()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var (code, output, _) = Run(repo, "close", "42", "--config", config);
        Assert.Equal(0, code);
        Assert.Equal("merged", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.Equal(3, repo.Git("rev-list", "--parents", "-n", "1", "main").Split(' ').Length);
    }

    [Fact]
    public void Close_Blocked_Exit1WithJsonAndOneErrorLine()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        File.WriteAllText(Path.Combine(repo.Root, "README.md"), "edited\n");
        var (code, output, err) = Run(repo, "close", "42", "--force", "--config", config);
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal("blocked", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.StartsWith("error: epic '42' not closed: 'main' is checked out at", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void Close_Conflict_Exit1()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        repo.Git("checkout", "-q", "epic/42-auth");
        repo.Commit("epic side", ("shared.txt", "epic\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main side", ("shared.txt", "main\n"));
        var (code, output, err) = Run(repo, "close", "42", "--config", config);
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal("conflict", SingleJsonLine(output).GetProperty("result").GetString());
        Assert.Contains("conflicts in shared.txt", err);
    }

    [Fact]
    public void Close_DryRun_Exit0_NothingChanged()
    {
        var (repo, config) = Opened();
        using var _ = repo;
        var before = repo.Sha("main");
        Assert.Equal(0, Run(repo, "close", "42", "--dry-run", "--config", config).Code);
        Assert.Equal(before, repo.Sha("main"));
    }

    [Theory]
    [InlineData("open", "42")]
    [InlineData("close")]
    [InlineData("nope")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.DoesNotContain('\n', err.TrimEnd());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicCliTests`
Expected: FAIL to compile (`Program.Run` not defined).

- [ ] **Step 3: Implement the CLI** (`src/Swarm.Epic.Cli/Program.cs`)

```csharp
using System.CommandLine;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Epic.Cli;

/// <summary>The <c>epic</c> tool: open, report and close epic branches.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line per command.</param>
    /// <param name="stderr">Receives warnings and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();
        var into = new Option<string?>("--into") { Description = "Target branch (default: the branch the epic was opened from)" };

        var id = new Argument<string>("id") { Description = "Epic id, e.g. 42 or 9933" };
        var slug = new Argument<string>("slug") { Description = "Lowercase hyphenated slug, e.g. auth" };
        var from = new Option<string?>("--from") { Description = "Base branch (default: config baseBranch)" };
        var kind = new Option<string?>("--kind") { Description = "Value for {kind} in epicTool.branchTemplate (e.g. feature, bugfix)" };
        var open = new Command("open", "Create the epic branch (no checkout) and its record; prints one JSON line") { id, slug, from, kind };
        common.AddTo(open);
        open.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var result = new EpicOpener(ctx.Repo, ctx.Config).Open(p.GetValue(id)!, p.GetValue(slug)!, p.GetValue(from), p.GetValue(kind));
            var progress = new Progress(stderr, ctx.Verbosity);
            foreach (var w in result.Warnings)
            {
                progress.Warn(w);
            }

            stdout.WriteLine(SwarmJson.Line(result));
            return ExitCodes.Ok;
        });

        var optionalId = new Argument<string?>("id") { Arity = ArgumentArity.ZeroOrOne, Description = "Epic id (default: every epic)" };
        var status = new Command("status", "Report run state, open tasks, worktrees and close blockers; prints one JSON line") { optionalId, into };
        common.AddTo(status);
        status.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var store = new EpicStore(new WorktreeManager(ctx.Repo, ctx.Config).State);
            IReadOnlyList<EpicRecord> epics = p.GetValue(optionalId) is { } one ? [store.Get(one)] : store.All();
            var assessor = new EpicAssessor(ctx.Repo, ctx.Config);
            stdout.WriteLine(SwarmJson.Line(new EpicStatusList(SwarmJson.SchemaVersion, epics.Select(e => assessor.Assess(e, p.GetValue(into))).ToList())));
            return ExitCodes.Ok;
        });

        var closeId = new Argument<string>("id") { Description = "Epic id" };
        var force = new Option<bool>("--force") { Description = "Waive waivable blockers (returned tasks, unfinished runs, unmerged worktrees)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Check and print the merge message; change nothing" };
        var deleteBranch = new Option<bool>("--delete-branch") { Description = "Delete the epic branch after merging" };
        var close = new Command("close", "Merge the epic --no-ff onto the active branch (never squashed); prints one JSON line") { closeId, into, force, dryRun, deleteBranch };
        common.AddTo(close);
        close.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var options = new CloseOptions(p.GetValue(into), p.GetValue(force), p.GetValue(dryRun), p.GetValue(deleteBranch));
            var r = new EpicCloser(ctx.Repo, ctx.Config).Close(p.GetValue(closeId)!, options);
            stdout.WriteLine(SwarmJson.Line(r));
            switch (r.Result)
            {
                case CloseResults.Blocked:
                    stderr.WriteLine(ToolException.Format($"epic '{r.Id}' not closed: {r.Blockers[0].Detail}", $"{r.Blockers.Count} blocker(s); see blockers"));
                    return ExitCodes.Returned;
                case CloseResults.Conflict:
                    stderr.WriteLine(ToolException.Format($"merging '{r.Branch}' into '{r.Into}' conflicts in {string.Join(", ", r.ConflictFiles)}", $"merge '{r.Into}' into the epic and resolve, then close again"));
                    return ExitCodes.Returned;
                default:
                    return ExitCodes.Ok;
            }
        });

        var root = new RootCommand("epic - open, report and close epic branches") { open, status, close };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}
```

- [ ] **Step 4: Run to verify pass, then the whole suite**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~EpicCliTests`
Expected: all PASS (10 tests).

Run: `cd <repo-root> && dotnet build src/Swarm.sln -warnaserror && dotnet test tests/Swarm.Tools.Tests -warnaserror && dotnet test tests/Swarm.Tests -warnaserror`
Expected: 0 warnings; all PASS: `Swarm.Tools.Tests` 458 (314 from Plans A and B, including the renamed `SquashFixture` callers, plus 144 new), `Swarm.Tests` 541 (renderer, unaffected). The full `Swarm.Tools.Tests` run takes several minutes (7.5 min observed on an idle machine).

- [ ] **Step 5: Pack both tools and smoke-test them through `dnx`, with a real `squash run`** (Git Bash; local repo only, no remote; PowerShell uses `dnx` instead of `dnx.cmd`; `Swarm.Squash` 0.1.1 is in the feed since Task 1 Step 7)

```bash
cd <repo-root>
dotnet pack src/Swarm.Worktree.Cli -c Release -o .docs/feed -warnaserror
dotnet pack src/Swarm.Epic.Cli -c Release -o .docs/feed -warnaserror
FEED="$(pwd -W)/.docs/feed"
SMOKE=/c/Development/agent-swarm-wt/smoke-c
rm -rf "$SMOKE" "$SMOKE-wt"
mkdir -p "$SMOKE" && cd "$SMOKE"
git init -q -b main
git config user.name s && git config user.email s@example.invalid
git commit -q --allow-empty -m init
dnx.cmd Swarm.Epic@0.1.0 --add-source "$FEED" -- open 42 auth; echo "exit $?"
dnx.cmd Swarm.Worktree@0.1.0 --add-source "$FEED" -- create 9933 login --epic 42; echo "exit $?"
cd "$SMOKE-wt/t-9933" && echo a > a.txt && git add a.txt && git commit -q -m "Login"
cd "$SMOKE"
dnx.cmd Swarm.Squash@0.1.1 --add-source "$FEED" -- run --task T1 --branch task/9933-login --epic 42-auth; echo "exit $?"
dnx.cmd Swarm.Worktree@0.1.0 --add-source "$FEED" -- prune --dry-run; echo "exit $?"
dnx.cmd Swarm.Worktree@0.1.0 --add-source "$FEED" -- prune; echo "exit $?"
dnx.cmd Swarm.Epic@0.1.0 --add-source "$FEED" -- status 42; echo "exit $?"
dnx.cmd Swarm.Epic@0.1.0 --add-source "$FEED" -- close 42; echo "exit $?"
git log --first-parent --format=%s main
git log -1 --format=%B main
```

The `squash run` lands the task the way batch would (the default config has `epicBranchTemplate` `epic/{epic}`, so the batch epic id of `epic/42-auth` is `42-auth`); it squashes onto the epic, which is not checked out, through its integration worktree `$SMOKE-wt/int-42-auth` (detached, unmanaged). Expected: `open` prints `"branch":"epic/42-auth"`, `"batchEpic":"42-auth"`, then `exit 0`. `create` prints `"branch":"task/9933-login"`, then `exit 0`. `squash run` prints one JSON line with `"ticket":"9933"`, `"empty":false`, `"exitCode":0`, then `exit 0` (the commit is `9933: Login` with `Ticket: 9933`, `Epic: 42-auth`, `Batch: 0`, `Swarm-Run: squash-...`). `prune --dry-run` prints one item with `"action":"remove"`, reason `merged (content)` and `"done":false`. `prune` prints `"removed":1`, the directory `$SMOKE-wt/t-9933` is gone, and `git branch --list task/*` is empty. `status` prints `"readyToClose":true`. `close` prints `"result":"merged"` and `"tickets":["9933"]`. `git log --first-parent` prints `Merge epic 42-auth (epic/42-auth) into main` and then `init`; the merge message contains `Epic: 42 (batch epic 42-auth)`, `Runs: 1` and `- 9933 (manual): Login`, and no `Commits naming another epic` line. No prompt appears. If a re-pack is needed, bump `<Version>` first and use that version. Record per-call times, then clean up: `rm -rf "$SMOKE" "$SMOKE-wt"`.

- [ ] **Step 6: Finish `docs/worktree-epic-tools.md`** (bump `updated:`). Add these sections, each stating only behaviour the tests pin:
  1. **epic**: the usage lines from this task's Interfaces; the branch model (workflow section 2): epic branch from the active branch, tickets squashed onto it by Plan B, `--no-ff` landing that is never squashed, history read with `git log --first-parent`; records in `<state>/epics/<id>.json`; idempotent open; `batchEpic` and how to choose `epicBranchTemplate` (e.g. `feature/{epic}` for example-org so that batch can address `feature/9933-login` as `--epic 9933-login`).
  2. **Close blockers** (`## Close blockers`): the C9 table (code, meaning, waivable), the C8 mechanics (temp worktree `close-<id>`, fast-forward of the checked-out active branch or a CAS ref update, never a fetch or push, "behind" means behind the last fetch), and the merge message format with the example from `MergeMessageTests.Build_ListsTicketsRunsUntrackedAndForeign`: own commits are those whose `Epic:` is the epic id or its batch epic id (the squash lander stamps the batch epic id), the leading ticket is dropped from `{ticket}: {title}` subjects, `Batch: 0` shows as `manual`, and `Runs: <n>` counts distinct `Swarm-Run:` values because batch numbers restart in every run.
  3. **Status for orchestrators**: every `EpicStatus` field, and that `epic status` and `epic close` share one assessor; that `batch-running` also covers a running `squash run` (shared per-epic lock); that a returned task landed later by `squash run` no longer counts as open (`EpicAssessorTests.ReturnedTask_LandedLaterBySquashRun_DoesNotBlock`); and that an open task's `reason` shows git's output for a land failure without files (`LandFailureWithoutFiles_ReportsGitOutputAsReason`).
  4. **Exit codes** for `epic` (C10), including that `close` prints JSON for 0 and 1 only.
  5. **Windows notes**: the 200-char guard, the MAX_PATH warning, path normalisation (worktrees identified by branch metadata), files held open during prune, and that `git worktree prune` is repository-wide.

- [ ] **Step 7: Append to `docs/decisions.md`** (newest last; bump `updated:`):

```markdown
## Worktree + epic production plan (<today>)

Plan: [2026-10-03-worktree-epic.md](plans/2026-10-03-worktree-epic.md).

| Decision | Choice |
|---|---|
| Config | Same `.swarm/batch.json`; new sections `worktree` and `epicTool` only (`epic` is taken by the batch epic id); reuses `worktreeRoot`, `baseBranch`, `stateDir`, `epicBranchTemplate`. |
| Branch naming | Templates with `{id}`, `{slug}`, `{kind}`; `allowedPrefixes` checked at config load and before creation; example-org uses `{kind}/{id}-{slug}` with `feature/`, `bugfix/` only. |
| Managed worktrees | Identified by `branch.<b>.swarm-*` git config, not by path; `<worktreeRoot>/t-<ticket>`. |
| Merged detection | Batch ledger, ancestry, or merge-tree content equality (squash-aware). |
| Prune safety | Locked never removed; dirty/unmerged/empty only with `--force`; `--dry-run`; per-item failures, exit 4. |
| Epic close | `--no-ff` merge in a temp worktree, fast-forward the checked-out active branch or CAS the ref; blockers shared with `epic status`; `--force` waives only run-state blockers. |
| Squash interplay | Own commits are those whose `Epic:` trailer is the epic id or its batch epic id (what the squash lander stamps); `Batch: 0` is shown as manual and runs are counted from `Swarm-Run:`; `squash run` writes no run state, so returned tasks it landed are cleared through the merged check; `batch-running` covers both tools' shared per-epic lock. |
| Versions | `Swarm.Worktree` 0.1.0 and `Swarm.Epic` 0.1.0 (NOT REAL placeholder ids); `Swarm.TestGate` 0.1.2, `Swarm.Batch` 0.2.1 and `Swarm.Squash` 0.1.1 re-packed because their strict config loaders reject the new sections. |
```

- [ ] **Step 8: Add to `docs/dnx-invocation-notes.md`** a subsection `### Worktree and Epic 0.1.0` at the end of the existing `## Companion tools` section (after `### Squash and Batch 0.2.0`), in the same style (numbered table rows continuing from 9, times, what was not tried), with the smoke commands from Step 5 and their observed results (only what was run). Bump `updated:`.

- [ ] **Step 9: Link** `docs/worktree-epic-tools.md` and this plan from `AGENTS.md` and `README.md`. Exact edits (quoted in fences so the doc sweeper does not resolve them from `docs/plans/`):
  - `AGENTS.md`: the "Shared docs (published wiki)" line currently ends with the squash tool and plan links; append:

    ```text
    ; [worktree and epic tools](docs/worktree-epic-tools.md) and plan [worktree + epic](docs/plans/2026-10-03-worktree-epic.md)
    ```

  - `README.md` status sentence, old:

    ```text
    Three more tools are built but not published: `testgate`, `batch` and `squash` (see [docs/batch-tools.md](docs/batch-tools.md), [docs/squash-tool.md](docs/squash-tool.md) and the plans [testgate + batch](docs/plans/2026-10-03-testgate-batch.md) and [squash](docs/plans/2026-10-03-squash.md)).
    ```

    new:

    ```text
    Five more tools are built but not published: `testgate`, `batch`, `squash`, `worktree` and `epic` (see [docs/batch-tools.md](docs/batch-tools.md), [docs/squash-tool.md](docs/squash-tool.md), [docs/worktree-epic-tools.md](docs/worktree-epic-tools.md) and the plans [testgate + batch](docs/plans/2026-10-03-testgate-batch.md), [squash](docs/plans/2026-10-03-squash.md) and [worktree + epic](docs/plans/2026-10-03-worktree-epic.md)).
    ```

  - `README.md`: after the bullet that starts `- Batched integration testing tools:`, add:

    ```text
    - Delivery tools: `worktree` (per-task worktrees from an epic branch, prune of merged work) and `epic` (open, status, `--no-ff` close). See [docs/worktree-epic-tools.md](docs/worktree-epic-tools.md); plan [2026-10-03-worktree-epic.md](docs/plans/2026-10-03-worktree-epic.md).
    ```

- [ ] **Step 10: Verify the doc sweeper is clean**

Run: `cd <repo-root>/spikes/04-doc-sweeper/a && dotnet run sweep.cs -- ../../../docs --today <today>`
Expected: no error and no `index-drift` warning for `worktree-epic-tools.md`, this plan, `decisions.md`, `dnx-invocation-notes.md`, `batch-tools.md` or `squash-tool.md`. Before this plan (at `3b32d30`) the sweep reports 20 findings with 2 errors, both in `swarm-renderer-ledger.md` (`missing-created`, `missing-updated`), plus `index-drift` warnings for `swarm-renderer-ledger.md` and this plan; the plan's warning must be gone after Step 9. Report the pre-existing errors; do not fix them here.

- [ ] **Step 11: Commit**

```bash
git add src tests docs AGENTS.md README.md
git commit -m "Add epic CLI; verify worktree and epic through dnx; document both tools" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** workflow section 2. The epic branch `epic/<id>-<slug>` with configurable naming is Tasks 1 and 7. The task branch `task/<ticket>-<slug>` from the epic branch via `git worktree add -b` is Task 4: tooling creates the worktrees because built-in isolation branches from the default branch, and Task 6 docs record why. The full `feature/`/`bugfix/` prefixes for example-org are covered by C3, Task 1 validation, and the Review Focus 1 tests. Workflow section 4: `--no-ff` landing that is never squashed, readable with `--first-parent`, is Task 9 (parent-count and first-parent assertions). The trailers `Ticket:`/`Epic:`/`Batch:` are listed in the merge message (Task 9). Workflow section 5: the worktree tool's create/list/prune is Tasks 4-6; the epic tool's open/close plus status is Tasks 7-10; the `dnx` packaging is Tasks 2, 6 and 10. The worktree root comes from config `worktreeRoot` (Task 4, reusing Plan A). The existing 200-char guard is reused through `StatePaths.Guard` (Task 4, Task 9). JSON output for orchestrators: every command prints one `schemaVersion: 1` line (Tasks 4-10). Prune of merged or abandoned worktrees, with `--force`, `--dry-run` and locked-worktree awareness, is Task 5. Close refuses on unlanded or red state (`returned.jsonl`/`summary.json` via Task 3, blockers in Task 8), on a dirty active branch, and on an active branch behind its remote (Task 8, without fetching). `epic status` comes from the state dir (Tasks 8, 10). Spec section 2 run state under the main worktree reuses `StatePaths.Resolve` (Plan A). Spec section 9 Windows risks are Review Focus 2 and 4. Out of scope: the squash lander and trailer stamping (Plan B), fetching or pushing remotes, multi-machine use, and automatic splitting of large epics into several chunks (workflow open question 5).
- **Shared-library use (no redefinition):** `GitRunner`, `ProcessResult`, `RepoLocator`/`RepoPaths`, `ToolException`/`ExitCodes`/`ToolErrors` (through `CliHost`), `SafeName`, `TextLines`, `FileTree`, `StatePaths`/`StateLayout`, `SwarmConfig`/`ConfigLoader`, `SwarmJson`, `JsonlFile`, `ReturnLedger`, `RunEvent`/`EventTypes`, `BatchSummary`/`LandedRecord`/`ReturnedEntry`/`FinalState`, `SlotSemaphore`/`SlotOptions`, `Progress`, `CliHost`, `CommonOptions`/`ToolContext`, `GitRunner.HeadsRef`, and the test fixtures `TempRepo`/`TempDir`/`TestConfig`/`JsonOutput` are all consumed with their executed signatures (reconciled against `3b32d30`; see the Reconciliation log). `SwarmConfig`/`ConfigLoader` get only the Task 1 additive change, placed after Plan B's. Plan B is consumed as data (trailers, the shared per-epic lock, no run state for `squash run`); its code is used only by tests (`SquashMessage`, `SquashRunner`).
- **Placeholder scan:** every code step has complete code. The deliberate human decisions are the real package ids and prefix (C11, csproj `Description`) and package metadata. `<repo-root>` and `<today>` are substitution markers, as in Plan A.
- **Type consistency:** names checked across tasks: `IBranchNaming`/`WorktreeSection`/`EpicSection`/`BranchTemplate.Render/Check/IsValidSlug/HasAllowedPrefix`; `GitWorktree`, `WorktreeList.Parse/Read/CheckedOut/SamePath`; `BranchMeta(Branch, Ticket, Base, ForkPoint)`, `BranchMetaStore.Write/ReadAll`; `EpicRecord` (11 fields, the same order in Tasks 2, 6, 7, 9), `EpicStore.Find/Get/Save/All/PathOf/Dir`; `RunRecord`, `TaskOutcome.Blocking`, `RunHistory.Load/ForEpic/Outcomes/LandedBranches`; `MergeCheck.LandedVia`, `MergeVia.*`; `CreateRequest`, `WorktreeEntry` (14 fields, the same order in Tasks 4, 5), `WorktreeManager.Create/List/PathFor/Root/Git/State`; `PruneDecision/PruneItem/PruneReport`, `Pruner.Decide/Prune`; `EpicNaming.BatchEpicId`, `EpicOpener.Open`; `EpicBlocker`, `BlockerCodes.*`, `EpicStatus`, `EpicAssessor.Assess`; `TrailerCommit` (6 fields, with `Runs`), `TrailerLog.Read/Parse`, `MergeMessage.Build(epic, batchEpic, into, commits)/Title/Tickets/ManualBatch`, `CloseOptions`, `CloseResults.*`, `EpicCloseResult`, `EpicCloser.Close`.
- **Review Focus:** each of the five lines names its tests, and each test is written out in its owning task (Tasks 1, 2, 3, 4, 5, 6, 7, 8, 9).
- **Fixed during review:** tests that need unmerged work commit a real file change. An empty commit merges as a no-op, so the content check would correctly call it merged. Two conditional collection expressions got explicit types (`var x = c ? list : []` does not compile).

## Reconciliation log

2026-10-05: this plan was written before Plans A and B ran. It was reconciled against the executed code on `swarm/worktree-epic` at `3b32d30`: Plan B (19 task commits plus its fix wave) on `swarm/testgate-batch` @ `05dd5ce` (Plan A, 21 task commits plus its fix wave). The ten "Plan C compatibility" items of Plan B's final review were applied first (marked B1-B10 below), each checked against the source. Then every Consumes block, signature, helper, path, version, count and doc anchor was compared with the code. Only this file changed.

**Verification.** All Task 1-10 code blocks were applied to a scratch clone of `3b32d30` outside the repo: whole files, plus the targeted edits exactly as written (`SwarmConfig`/`ConfigLoader` old -> new, the Epic CLI csproj, the test csproj references, `dotnet sln add`, the three version bumps and the `SquashFixture` rename). Results: `dotnet build src/Swarm.sln -warnaserror` gave 0 warnings and 0 errors, but only after the `SquashFixture` rename (row 2.4). The 143 new tests plus the existing `ConfigLoaderTests` and `SquashConfigTests` passed 180 of 180, and the full suites passed (row 10.9). Row 9.8 then added a 144th test, which was applied and run with its class. `dotnet pack` produced `Swarm.Worktree.0.1.0`, `Swarm.Epic.0.1.0`, `Swarm.TestGate.0.1.2`, `Swarm.Batch.0.2.1` and `Swarm.Squash.0.1.1`. The Task 10 Step 5 `dnx` smoke test, including the real `squash run`, was run from that scratch feed in a scratch repository: every expected output was observed, at 6 to 10 s per call. The scratch repository was deleted afterwards, and so were the `~/.nuget/packages/swarm.{epic,worktree,squash}` cache entries it created, so execution will not be served stale packages.

Git 2.54.0.windows.1 experiments, in scratch directories outside the repo:
- `git init` creates reftable repositories (`rev-parse --show-ref-format` prints `reftable`).
- `git worktree add -q -b task/9933-login <path> <epic sha>` works and leaves the epic checked out nowhere.
- `git branch -D` removes the branch's `swarm-*` config section.
- `git config --get-regexp` keeps the branch (subsection) case and slashes.
- The porcelain `locked <reason>` and `prunable gitdir file points to non-existent location` lines are parsed as expected.
- A locked worktree whose directory is gone is **not** reported `prunable`. It survives `git worktree prune` and blocks `worktree add` at its path (`is a missing but locked worktree`).
- `merge-tree --write-tree <epic> <task>` returns the epic's tree for squash-landed content.
- With a file held open (`FileShare.None`), `git worktree remove` exits 255 with or without `--force`. By then it has already deleted the registration (row H7).

Not proven: the Task 10 Step 10 doc sweep and the doc text of Tasks 1, 6 and 10 (no docs were written; the sweeper was run on the current docs to record the baseline), and behaviour on non-Windows hosts.

| # | Where | What was wrong | Change | How verified |
|---|---|---|---|---|
| H1 | Header, Spec line | Said Plan B is "not a dependency" and gave no stacking (B10); front matter `updated` stale | States Plan B is merged first and this branch is stacked on `swarm/squash` @ `3b32d30`, on `swarm/testgate-batch` @ `05dd5ce`. Links `batch-tools.md`, `squash-tool.md` and both decision entries; `updated: 2026-10-05`. "Out of scope: the squash lander and trailer stamping (Plan B)" kept | `git log --oneline 74f8f59..HEAD`; docs read |
| H2 | Tech Stack | No observed git version; the reftable default was not known | Notes git 2.54.0.windows.1 and that `git init` creates reftable repos | `git --version`; scratch `rev-parse --show-ref-format` |
| H3 | C1 | Said "Plan A Tasks 1-8"; put "the CLI host" in `Swarm.RunState` (it is in `Swarm.RunState.Cli`, with `CtrlCScope`, `CommonOptions` and `ToolContext`); had no `HeadsRef` rule; said nothing about using Plan B code | Rewritten with the executed namespaces and members. Adds a `GitRunner.HeadsRef` rule (no `"refs/heads/" +` literals). Production code references no Plan B project; tests may use `SquashMessage`/`SquashRunner` | Read `src/Swarm.Git`, `src/Swarm.RunState`, `src/Swarm.RunState/Cli` |
| H4 | C2 | Said "Plan B adds only its own section"; said "re-pack every tool" without a list or versions (B8, B9) | Names Plan B's top-level `lander` key and `squash` section, and places Plan C's two hunks after them. Bumps `Swarm.TestGate` 0.1.1 -> 0.1.2, `Swarm.Batch` 0.2.0 -> 0.2.1 and `Swarm.Squash` 0.1.0 -> 0.1.1 (all three loaders are strict) | Read `SwarmConfig.cs`, `ConfigLoader.cs` and the three csproj files; packed all three at the new versions |
| H5 | C5 | Assumed the Ledger check sees every landing (B5) | Notes that `squash run` writes no run state and that batch prunes finished run folders beyond `keepRuns`; points to the Task 8 fix | Read `SquashRunner.cs` (no `RunDirectories` use) and `BatchEngine.Run` (`RunDirectories.Prune`) |
| H6 | C9 | `batch-running` described only batch (B6) | Gives the lock path, which `batch run` and `squash run` share, and the detail text `a batch or squash run holds epic '<id>'` | Read `BatchEngine.Run` and `SquashRunner.Run` (both use `state.BatchLockDir(config.Epic)`, one slot, no wait) |
| H7 | C6 | Assumed a held-file failure leaves the worktree registered | Records the observed git 2.54 behaviour: exit 255; the registration and `.git` file are already gone; the directory is left unregistered; the branch and its metadata are kept | Scratch experiment above; `PrunerTests.HeldFile_...` now pins it (row 5.2) |
| H8 | File Structure | Missed the three csproj bumps and the doc files that Tasks 1 and 10 change | Rows added | n/a |
| H9 | Global Constraints (tests) | Located the fixtures by Plan A task numbers; no `SWARM_TEST_ROOT`, `JsonOutput` or reftable note | Names `tests/Swarm.Tools.Tests/Support`, `TestPaths`' `SWARM_TEST_ROOT`, `JsonOutput`, and that ref locks must come from `TempRepo.LockRef` (reftable) | Read `TestPaths.cs`, `TempRepo.LockRef`; `StaleRefLock_...` passes on a reftable repo |
| 1.1 | Task 1 Files, Interfaces | Old placement ("end of `SwarmConfig`, after `KeepRuns`"); Consumes listed names only | Files list the csproj and doc edits. Consumes quote `ConfigLoader.Parse/Validated/Check` and the strict-JSON behaviour | Read `ConfigLoader.cs`, `SwarmConfig.cs` |
| 1.2 | Task 1 Steps 4-5 | "After `KeepRuns`" and "before `return e;`" would interleave with Plan B's `Lander`/`Squash` and `SquashConfig.Check` (B8) | Exact old -> new edits after `Squash` and after `e.AddRange(SquashConfig.Check(c.Squash));` | Applied in the scratch clone; built |
| 1.3 | Task 1 Step 6 | The filter missed the config tests Plan B added and the CLI tests that round-trip `TestConfig.Write` | Adds `SquashConfigTests` and `SquashCliTests`; expected count 84 | Ran: 29 new + 24 + 13 + 18 pass |
| 1.4 | Task 1 (new Step 7) | No re-pack step (B9) | Bumps and packs all three tools. Exact edits to `batch-tools.md` (versions and a `worktree`/`epicTool` config row) and `squash-tool.md` (versions). Step 8 commits them | Packed `Swarm.TestGate.0.1.2`, `Swarm.Batch.0.2.1` and `Swarm.Squash.0.1.1` in the scratch clone; doc lines read |
| 2.1 | Task 2 Interfaces | Consumes cited Plan A task numbers only | Quotes `GitRunner.Run/Try/Lines`, `ProcessResult`, the exceptions of `SwarmJson.Read` and the `TempRepo` members used | Read sources |
| 2.2 | Task 2 Step 1 csproj | The description wording differed from the executed `Swarm.Squash.Cli.csproj` | Adds "dependency-confusion risk", as Plan B does | Read csproj |
| 2.3 | Task 2 Step 1 test csproj | The insertion point and "Plan A's two CLIs" were out of date | After the `Swarm.Squash.Cli` reference; "three existing CLIs", with the `SquashProgram` alias as the example | Read the test csproj and `SquashCliTests.cs` |
| 2.4 | Task 2 Step 1 (new), Files | Not foreseen: the namespace `Swarm.Worktree` hides `SquashFixture.Worktree(repo)`, which Plan B's tests call through `using static`; 33 `CS0118` errors | Renames the fixture method to `IntegrationFor` in `SquashFixture.cs`, `SquashLanderTests.cs`, `SquashLanderEdgeTests.cs` and `TestedChainTests.cs` (exact `sed` plus a check). Notes the same hazard for a bare `Epic` | The scratch build failed with 33 errors and succeeded after the rename; the renamed Plan B tests pass in the full run |
| 2.5 | Task 2 Step 7 | No count | 10 tests; notes that the build is clean only after 2.4 | Ran |
| 3.1 | Task 3 Interfaces | Consumes by name only | Quotes `BatchSummary` (30 fields, in order), `ReturnedEntry` (16), `LandedRecord`, `RunEvent`, the `run-start` payload and the doubled `.docs/runs/runs/<id>` layout. States that a run is finished iff `summary.json` exists | Read `OutputTypes.cs`, `EventLog.cs`, `RunDirectories.cs`, `StatePaths.cs` and `BatchEngine.Run`; `docs/batch-tools.md` marks the layout "verified" |
| 3.2 | Task 3 `RunStateFixture` | The `run-start` data `{ tasks, epicBranch }` and lander `fast-forward` differed from what batch writes | `{ tasks, epic, epicBranch, mode, lander }` and lander `squash`, like `BatchEngine.Run` | Tests pass |
| 3.3 | Task 3 `MergeCheck` | `"refs/heads/" +` literals | `GitRunner.HeadsRef` | Build; tests (12) |
| 4.1 | Task 4 Interfaces | Consumes by name; did not mention batch's `int-<epic>` worktree in the same root | Quotes the `StatePaths`, `RepoPaths`, `TempRepo` and `TestConfig` members. Notes that `int-<epic>` is detached and unmanaged | Read sources; in the smoke test, prune left `int-42-auth` alone |
| 4.2 | Task 4 `WorktreeManager` | Five `"refs/heads/"` literals | `GitRunner.HeadsRef` (with `w.Branch!` in the rev-list range) | Build -warnaserror; tests (14) |
| 5.1 | Task 5 Interfaces | Consumes by name | Quotes `ProcessResult` and the helpers | Read sources |
| 5.2 | Task 5 `HeldFile_FailsItemKeepsBranchContinues` | Did not pin what git leaves behind | Asserts that the held directory still exists and is no longer listed (C6) | Test passes on Windows; scratch experiment |
| 6.1 | Task 6 Interfaces | `CommonOptions`, `CliHost` and `ToolContext` had no namespace or signatures | Quotes the `Swarm.RunState.Cli` members and their behaviour; no `CtrlCScope` is needed | Read `CliHost.cs`, `CommonOptions.cs` |
| 6.2 | Task 6 tests (also Task 10) | Redefined `SingleJsonLine`, which already exists as `Support/JsonOutput.SingleJsonLine` (Plan B's log row 13 fixed the same thing) | `using static Swarm.Tools.Tests.Support.JsonOutput;`; the local helper and the `System.Text.Json` using are removed | Build; tests (11) |
| 6.3 | Task 6 Step 6 docs | Re-pack advice without versions; no `squash-tool.md` link; no held-file or `squash run` note | Adds the minimum versions (0.1.2 / 0.2.1 / 0.1.1), the link, the C6 leftover-directory behaviour and the C5 note | n/a (doc text) |
| 7.1 | Task 7 Interfaces | Consumes by name; no reason given for "no checkout" | Quotes `SwarmConfig.EpicBranch`. Notes `RepoChecks.EnsureEpic`: batch and `squash run` refuse a checked-out epic with exit 3 | Read `RepoChecks.cs`, `SwarmConfig.cs` |
| 7.2 | Task 7 `EpicOpener` | Three `"refs/heads/"` literals | `GitRunner.HeadsRef` | Build; tests (13) |
| 8.1 | Task 8 Interfaces | Did not quote `SlotSemaphore`, the lock sharing or the test dependencies | Quotes `SlotSemaphore(...).Status()`/`SlotHolder`, `BatchLockDir` (shared lock), `MergeCheck`, and `SquashRunner`/`ToolContext` | Read `SlotSemaphore.cs`, `SquashRunner.cs` |
| 8.2 | Task 8 fixture | The epic commit was stamped `Epic: 42`, which Plan B never writes (B1) | `9933: work` with `Epic: 42-auth` and `Swarm-Run: r0` | Tests pass |
| 8.3 | Task 8 `BatchRunning` | The text said "a batch run" (B6) | `a batch or squash run holds epic '<id>'`, plus the doc comments; `LiveBatchLock_...` asserts the text | Test passes |
| 8.4 | Task 8 `EpicAssessor` | A task that batch returned and a human landed with `squash run` blocked close forever (B5) | Open tasks leave out outcomes whose branch exists and for which `MergeCheck.LandedVia(git, branch, epic.Branch, ledger)` is non-null. New test `ReturnedTask_LandedLaterBySquashRun_DoesNotBlock` runs a **real** `SquashRunner` | Test passes |
| 8.5 | Task 8 `TaskReturn.Reason` | Showed batch's generic `land conflict with the epic tip` for land failures (B7) | `ReasonOf` returns `GitOutput` (one-lined) for `stage: land` with no files. New test `LandFailureWithoutFiles_ReportsGitOutputAsReason` uses the squash lander's real no-ticket text | Read `BatchEngine.ReturnConflict` and `SquashLander.NoTicketReason`; test passes |
| 8.6 | Task 8 `EpicAssessor` | Six `"refs/heads/"` literals | `GitRunner.HeadsRef` | Build; tests (15) |
| 9.1 | Task 9 Interfaces | Did not describe what Plan B writes (B4) | New "What Plan B writes" paragraph: the subject template, the trailer order, the batch epic id in `Epic:`, per-run `Batch:`, and `0` = manual | Read `SquashMessage.cs` and `docs/squash-tool.md#what-lands` |
| 9.2 | Task 9 `TrailerCommit`/`TrailerLog` | `Swarm-Run` was not read (B3) | `TrailerCommit` gains `Runs`; `Parse` reads `Swarm-Run` | Tests pass |
| 9.3 | Task 9 `MergeMessage.Build` | Every squash-landed commit was listed as "naming another epic", because only `epic.Id` counted (B1) | New parameter `batchEpic`. A commit is the epic's own when its `Epic:` is `epic.Id` or `batchEpic`. Header `Epic: 42 (batch epic 42-auth)`. `EpicCloser` passes `status.BatchEpic`. A fixture with `Epic: 42` still counts as the epic's own | `Build_ListsTicketsRunsUntrackedAndForeign`; `EpicCloserTests.ActiveCheckedOut_...` asserts there is no "another epic" line; smoke test |
| 9.4 | Task 9 `MergeMessage` | `- 9933 (batch 1): 9933: Login form` showed the ticket twice (B2) | `MergeMessage.Title` drops a leading ticket followed by `:`, space or `-`, and keeps the subject when nothing would be left. This is the rule of Plan B's `SquashMessage.Title`, which `Swarm.Delivery` cannot reference (C1) | `Title_DropsALeadingTicketLikeTheSquashLander` (5 cases) |
| 9.5 | Task 9 `MergeMessage` | `Batches: 1, 2` is ambiguous across runs, and `Batch: 0` means manual (B3) | The header line `Runs: <count of distinct Swarm-Run values>` replaces `Batches:`; each line says `(batch N)` or `(manual)` | Tests; the smoke message has `Runs: 1` and `- 9933 (manual): Login` |
| 9.6 | Task 9 `MergeMessageTests` | Fixtures were not Plan B-shaped; no test against the real trailer block (B4) | Fixtures rewritten. `Read_ParsesTheTrailerBlockPlanBWrites` commits a message built by Plan B's own `SquashMessage.Build` (two tasks, `Squashed commits:`, `Source-Commit`, `Co-authored-by`), so a format change breaks it. That is stronger than a copied literal | Tests pass (9) |
| 9.7 | Task 9 `EpicCloserTests` | Fixtures used `Login form` and `Epic: 42` (B1, B2) | `9933: Login form`, `Epic: 42-auth`, `Swarm-Run`; asserts the stripped line and that there is no foreign-epic line | Tests pass |
| 9.8 | Task 9 `EpicCloser.MoveActive` | Reported any failed CAS `update-ref` as "moved during close". Plan B's fix wave (`479af1b`) showed that a stale ref lock (including reftable's `tables.list.lock`) must report git's reason | Re-reads the ref. If it is unchanged: `could not move '<into>': <git's reason>`, with the stale-lock hint. If it changed: "moved". New test `StaleRefLock_ReportsGitsReasonNotMoved` uses `TempRepo.LockRef` | Row 10.9 |
| 9.9 | Task 9 `EpicCloser` and `TrailerLog` callers | Three `"refs/heads/"` literals | `GitRunner.HeadsRef` | Build; tests (20) |
| 10.1 | Task 10 Interfaces, tests | Consumes cited Plan A only; local `SingleJsonLine`; fixture used `Epic: 42` | Namespaces, `JsonOutput` and a Plan B-shaped fixture | Tests pass (10) |
| 10.2 | Task 10 Step 5 smoke test | Used a hand-made trailer commit plus `update-ref` (B10, optional) | A real `dnx Swarm.Squash@0.1.1 -- run --task T1 --branch task/9933-login --epic 42-auth`. Expected outputs include `merged (content)`, `Epic: 42 (batch epic 42-auth)`, `Runs: 1` and `- 9933 (manual): Login` | Run from the scratch feed; every expected output observed |
| 10.3 | Task 10 Steps 6-9 | Old test name; no squash interplay in the docs or decisions; the dnx-notes heading was "create if missing" (it exists, with a `### Squash and Batch 0.2.0` subsection); the AGENTS and README edits were paraphrased | The doc items name the new tests and rules. Two decision rows (`Squash interplay`, `Versions`). `### Worktree and Epic 0.1.0` goes after the existing subsection. Exact old -> new text for AGENTS and README | Read `docs/dnx-invocation-notes.md`, `AGENTS.md`, `README.md` and `docs/decisions.md` |
| 10.4 | Task 10 Step 10 | No baseline given | Records the current sweep: 20 findings and 2 errors (both in `swarm-renderer-ledger.md`), with `index-drift` for that file and this plan | Ran `spikes/04-doc-sweeper/a/sweep.cs -- ../../../docs --today 2026-10-05` |
| 10.9 | Task 10 Step 4 | No counts; the review baseline (306 tools tests) predates Plan B's last fix commits | `Swarm.Tools.Tests` 458 = 314 existing + 144 new; `Swarm.Tests` 541 | Scratch full runs: `Swarm.Tools.Tests` 457 of 457 (7 m 26 s) and `Swarm.Tests` 541 of 541 before row 9.8 added one test; then `EpicCloserTests` + `MergeMessageTests` 20 of 20 with it |
| S1 | Self-review | Said "Plan B is not referenced"; the type list lacked `Runs`/`batchEpic` | Updated | n/a |
| S2 | Task 1 Step 7, Task 10 Step 9 (new quotes) | The first draft of the exact doc edits quoted Markdown links in double-backtick spans, which the doc sweeper resolves from `docs/plans/` (17 `broken-link` errors) | The quoted rows and sentences moved into `text` fences, as Plan B's `3b32d30` did for its quoted links | Sweeper on the reconciled docs: 20 findings, 2 errors (the pre-existing `swarm-renderer-ledger.md` pair), only the expected `index-drift` warning for this plan |

Checked against the executed code and left unchanged:
- `SafeName` (including dots), `StatePaths.Guard` (200), `StateLayout.BatchLockDir`, `SlotOptions.From` and `SlotSemaphore.Acquire/TryAcquire`.
- `RepoLocator.Locate` (exit 3 outside a repo), `ToolErrors.Handle` (one stderr line) and `CliHost.Invoke` parse errors (exit 2, one line).
- `JsonlFile.ReadAll` (a missing file reads as empty), `ReturnLedger.New/ReadLatest`, the `FinalState`/`ReturnKind`/`ReturnStage` values and `LandedRecord`.
- The test fixtures: `TempRepo` (`Create`, `Git`, `RunGit`, `Commit`, `Branch`, `Epic`, `Sha`, `LockRef`, `Sandbox`, `StateDir`, `WorktreeRoot`), `TempDir` and `TestConfig.For/Write`.
- The strict loader's messages (`'branchTemplte'`, and `invalid config` for `null`).
- The `EpicRecord`, `WorktreeEntry` and `EpicStatus` shapes and the CLI command shapes.
- The decision-table and sweeper commands, and rulings C3, C4, C7, C8, C10 and C11.

Executed-code observations for whoever executes this plan (not changed here):
1. `RunDirectories.Prune` keeps the newest `keepRuns` finished runs across **all** epics. A busy epic's batch runs can therefore delete another epic's run folders, and with them the returned-task evidence that `tasks-returned` relies on.
2. `IntegrationWorktree.Ensure` runs a repository-wide `git worktree prune` at the start of every batch and `squash run`. A task worktree whose directory was deleted is therefore deregistered before `worktree prune` sees it; its branch and `swarm-*` metadata stay, and `worktree list/prune` only enumerate registered worktrees. A held-file removal failure (C6) leaves the same state.
3. Batch still says `another batch run holds epic` when a `squash run` holds the lock. It also still records a land failure's reason only in `gitOutput` (Plan B final review I1); Task 8 works around this.
