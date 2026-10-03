---
created: 2026-10-03
updated: 2026-10-03
status: current
---
# Testgate + Batch (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote spike 1B (`spikes/01-batched-tests/b/{testgate,batch}.cs`) to two `dnx` tools, `testgate` (machine-wide test-slot gate) and `batch` (adaptive batched integration with bisect, stop-on-conflict and rebase-copy requeue), on top of a shared run-state library that Plans B (squash) and C (worktree + epic) reuse.

**Architecture:** Two shared libraries: `Swarm.Git` (process runner with process-tree kill, git runner with one-line errors, repo/main-worktree locator, exit codes, error type) and `Swarm.RunState` (config, state-dir layout, lock-file slot semaphore, JSON output types with `schemaVersion`, JSONL ledgers/events, run-dir retention, shared CLI host). Two engine libraries: `Swarm.Gate` (acquire slot, run command, release) and `Swarm.Batching` (tasks file, stack units, pre-batching, integration worktree, bisect, landing through an injectable `ILander`, rebase-copy requeue). Two thin `System.CommandLine` CLIs (`Swarm.TestGate.Cli` -> package `Swarm.TestGate`, command `testgate`; `Swarm.Batch.Cli` -> package `Swarm.Batch`, command `batch`). `batch` calls the gate in-process (no `dnx` hop per suite: each `dnx` call costs 8-25 s).

**Tech Stack:** .NET 10 (`global.json` 10.0.401), C# with nullable, xUnit 2.9.3, System.CommandLine 2.0.0, System.Text.Json, git >= 2.31 (`--path-format=absolute`), NuGet tool packaging (`PackAsTool`).

**Spec:** [docs/specs/2026-10-02-agent-swarm-design.md](../specs/2026-10-02-agent-swarm-design.md) (section 3, delivery mechanics); decisions: [docs/decisions.md](../decisions.md) (Stage 2 run state, Stage 3 test/merge mechanics, "Spike 1 completion"); [docs/workflow.md](../workflow.md) section 3; spike evidence and promotion list: [spikes/01-batched-tests/b/RESULTS.md](../../spikes/01-batched-tests/b/RESULTS.md), [PROMOTION.md](../../spikes/01-batched-tests/b/PROMOTION.md); dnx facts: [docs/dnx-invocation-notes.md](../dnx-invocation-notes.md). House style reference: [2026-10-03-swarm-definition-renderer.md](2026-10-03-swarm-definition-renderer.md).

## Global Constraints

Project-wide (every task):

- Target `net10.0`; build and test with `-warnaserror` and **zero warnings** (xUnit analyzers included: use `Assert.Single`/`Assert.Empty`/`Assert.Contains`, no `.Result`/`.Wait()` in tests, `async Task` for concurrent tests).
- Every library and CLI project imports `src/Swarm.Tooling.props` (`GenerateDocumentationFile`, `TreatWarningsAsErrors`): **XML doc comments on every public API**, with `<param>`, `<returns>`, `<typeparam>`, `<exception>` where applicable (positional records document every parameter). Comments StyleCop-compatible (blank line before a standalone `//`).
- DRY: shared behaviour lives in `Swarm.Git` / `Swarm.RunState`; no copy of the process, git, path-guard, JSON or retry logic anywhere else.
- **LF line endings** in every file and every generated file (`.gitattributes` already has `* text=auto eol=lf`); JSON files written via `SwarmJson.WriteFile` (LF, trailing newline, atomic replace).
- Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- `dnx`: never `--yes`; Git Bash uses `dnx.cmd`; tool args go after `--`; **bump `<Version>` on every re-pack** (dnx reuses `~/.nuget/packages/<id>/<version>`; a re-pack of the same version runs old code).
- **Package ids `Swarm.TestGate` and `Swarm.Batch` are placeholders and NOT REAL**: unclaimed on nuget.org; only ever run with `--add-source <your feed>`. They stay placeholders until the human reserves an owned NuGet id prefix; package `Description` says so.
- Tests never run a real suite: the test command is `tests/Swarm.FakeSuite` (Task 2), repos are temp repos from `TempRepo` (Task 2), sandboxes live under `%TEMP%\swt\<8 hex>` (override with env `SWARM_TEST_ROOT`).
- Errors: exactly ONE stderr line `error: <what> (<hint>)`. stdout: exactly one JSON line per command (`testgate run|status|reclaim`, `batch run`); human progress and child output go to stderr.

Rulings (each confirms or adjusts a PROMOTION.md "spike default"; cost-if-wrong in brackets):

- **R1 Exit codes** (`ExitCodes`, schema 1, append-only): 0 ok / all landed; 1 work returned (batch) or gated command failed (testgate); 2 usage/config (incl. over-long paths); 3 bad input (tasks file, missing or checked-out epic); 4 environment (git, worktree, cannot start command, lander contract violation, concurrent batch on same epic, cancelled); 5 gate wait timeout. `testgate run` maps any non-zero child exit to 1 (child code is in the JSON `exitCode`). [Scripts wanting the raw child code must read the JSON.]
- **R2 Exit 0 after rebase:** a task that landed after an automatic rebase counts as landed; exit 0 if every task landed. A task left `no-op-after-rebase` is not landed (exit 1). [A human might want to see rebased tasks: they are still listed in `returned.jsonl` with `final: rebased-and-landed`.]
- **R3 Config:** `.swarm/batch.json` in the **main** worktree (or `--config <file>`); flags win over file, file over defaults; unknown keys rejected; `//` comments and trailing commas allowed. Defaults: `slots 2, batch {start 4, min 2, max 8}, expirySec 60, heartbeatSec 5, pollMs 200, maxWaitSec 3600, baseBranch main, epic E1, epicBranchTemplate "epic/{epic}", testCommand ["dotnet","test"], stateDir ".docs/runs", worktreeRoot null (= <main parent>/<repo>-wt), maxRebaseAttempts 1, prebatch true, keepRuns 20`. Validation: slots >= 1; 1 <= min <= start <= max <= 64; heartbeat >= 1 and expiry >= 3 x heartbeat; pollMs 10..60000; maxWaitSec >= 0; testCommand non-empty; epic a safe name; template contains `{epic}`; maxRebaseAttempts 0..3; keepRuns >= 1; worktreeRoot absolute if set; state dir and worktree paths <= 200 chars. [Per-worktree config is impossible; a typo'd key fails loudly instead of being ignored.]
- **R4 JSON schema:** every stdout object and every JSON/JSONL record starts with `schemaVersion: 1`, camelCase keys. `returned.jsonl` is append-only; each line is a full snapshot of one task's return record and **the last line per task wins** (`ReturnLedger.ReadLatest`). [Readers that take the first line per task see stale states.]
- **R5 Logging:** progress on stderr with `--verbosity quiet|normal|detail` (child/suite output only at `detail` for batch, `normal` for testgate); `runs/<runId>/events.jsonl`; per-suite logs `runs/<runId>/logs/suite-NNN.log`; retention keeps the newest `keepRuns` **finished** runs (those with `summary.json`), never unfinished/crashed ones; testgate appends to `<state>/testgate.events.jsonl`. [Crashed runs accumulate until removed by hand.]
- **R6 Gate scope:** one machine. Slot locks are files in the state dir on a local disk; network shares are unsupported (documented, not detected). On Windows a live holder's lock cannot be deleted (handle opened without `FileShare.Delete`), so the two-reclaimer race is closed there; on Linux/macOS it remains as documented in the spike. [Multi-machine runners need a different backend: out of scope, spec section 8.]
- **R7 Wait policy:** polling every `pollMs`, no FIFO fairness; bounded by `maxWaitSec` (default **3600**, `0` = forever) -> exit 5. Spike had no bound (a wedged live holder blocked forever). [A legitimately long queue times out after an hour; raise `--max-wait`.]
- **R8 Slot lock:** the "lock directory" (decisions Stage 3) is `<state>/slots/`; slot k is `slot-k.lock` created atomically with `FileMode.CreateNew`, holding `{pid, host, command, acquiredUtc, processStartUtc}`; heartbeat touches mtime through the held handle; a lock older than `expirySec` is stale and reclaimed. Spike-proven; no `DeleteOnClose`. [A killed holder blocks its slot for up to `expirySec`; `testgate reclaim --force` frees dead-pid locks immediately.]
- **R9 Spike 1 completion decisions, applied:** missing task branch -> that task returned (`bad-input`), rest run, exit 1; dependents of a returned task are returned too (`blocked-by-dependency`). Conflict -> automatic rebase of a COPY `rebased/<epic>/<task>` onto the epic tip, at most `maxRebaseAttempts` (1), clean -> requeued at the queue front, conflict again -> `needs-worker`; the worker's branch is never modified. Red -> the later task in queue order is blamed (bisect convention). File-name pre-batching stays as a hint.
- **R10 Stacks:** tasks declare `dependsOn` (ids of EARLIER tasks in the same file, else bad input exit 3); tasks connected by `dependsOn` form one **unit**, placed at its first member's position, never split by pre-batching or bisect, merged/landed together in queue order; a red unit returns all members; a conflicting multi-task unit goes straight to `needs-worker` (no auto-rebase). [Stacks that could have been rescued by rebase need a worker.]
- **R11 Default lander:** `FastForwardLander` moves the epic ref (compare-and-swap) to the tested integration commit (one `--no-ff` merge commit per task, tool identity `swarm-batch`). Plan B replaces it with a squash lander through `ILander`. Batch verifies every lander: when all tasks land, the landed tree must equal the tested tree, else exit 4. [Until Plan B, epic history has merge commits, not squashed ticket commits.]
- **R12 Epic branch:** batch requires the epic branch to exist and not be checked out in any worktree; it never creates or resets it (the spike's `branch -f` reset was destructive). Plan C creates epics. [A first run needs `git branch epic/E1 main`.]
- **R13 Integration worktree:** `<worktreeRoot>/int-<epic>`, detached, reused across runs; crash leftovers (index.lock, HEAD.lock, in-progress merge/rebase) are cleaned because the batch lock is held; rebases run in the same worktree (spike used a second one). `git clean -fd` (not `-x`) keeps ignored build output warm. [Ignored files that a task starts tracking can make a merge fail as a conflict.]
- **R14 Touches:** derived from git as `git diff --name-only <epic tip>...<task branch>` (the spike used the base branch), compared case-insensitively; `touches` in tasks.json is accepted and ignored. [Case-sensitive repos with `A.cs` and `a.cs` get a false overlap: costs at most an extra batch.]
- **R15 Repo context:** both tools must run inside a git worktree (any worktree of the repo); `testgate run --cwd <dir>` runs the command elsewhere. [Gating a command outside any repo needs a repo cwd.]
- **R16 Concurrency:** one batch run per epic per machine (lock `<state>/locks/batch-<epic>/`); a second run fails fast with exit 4. Different epics share the slot budget. [A queued second run must be retried by its caller.]
- **R17 CLI surface:** stdout is always the JSON object (no `--json` flag); `measure` mode dropped; `--experimental-no-prebatch` and `--experimental-fixed N` kept for experiments; `--mode batched|serial`. System.CommandLine is used here (the renderer declined it; these CLIs have sub-commands, `--` passthrough and shared options), with response files disabled so `@x` reaches the gated command. [One extra dependency.]

## Review Focus

1. **Over-long Windows paths** (state dir, worktree root or lock dir over 200 chars) -> one-line exit 2 before any git command, directory, lock or worktree is created. Tests: `StatePathsTests.TooLongStateDir_Exit2OneLine` (Task 4), `TestGateCliTests.LongStatePath_Exits2OneLine` (Task 8), `BatchEngineTests.LongWorktreeRoot_Exit2_NothingCreated` (Task 11).
2. **Gated commands that spawn grandchildren** (`dotnet test` -> testhost, MSBuild nodes) -> a timeout or cancel kills the whole tree; a grandchild still holding the output pipes does not hang the runner; the slot is released even when the command cannot start. Tests: `ProcessRunnerTests.Timeout_KillsWholeTree`, `OrphanHoldingPipes_DoesNotHang`, `Cancel_KillsTreeAndThrows` (Task 3), `GateRunnerTests.CommandCannotStart_ReleasesSlot` (Task 8).
3. **Leftovers from a crashed run** (existing integration worktree, stale `index.lock`, an in-progress merge, read-only git object files, a killed holder's slot lock) -> reused, cleaned or reclaimed, never "File exists" or a hang. Tests: `FileTreeTests.DeletesReadOnlyFiles` (Task 1), `SlotSemaphoreTests.StaleLock_IsReclaimed` (Task 7), `IntegrationWorktreeTests.Ensure_CleansIndexLockAndLeftoverMerge` (Task 10), `BatchEngineTests.SecondRun_ReusesIntegrationWorktreeWithStaleLock` (Task 11).
4. **Concurrent processes on one machine** (parallel appenders to one JSONL file, a reclaimer versus a live holder, two batch runs on one epic, more workers than slots) -> no lost or torn lines, a live lock is never deleted, the second batch fails fast, never more holders than slots. Tests: `JsonlFileTests.ParallelAppends_AllLinesValid` (Task 6), `SlotSemaphoreTests.LiveLock_CannotBeDeletedOnWindows`, `ConcurrentHolders_NeverExceedSlots`, `Reclaim_RemovesStaleAndDeadButNotLive` (Task 7), `BatchEngineTests.ConcurrentRunOnSameEpic_FailsFast` (Task 11).
5. **Case, encoding and shim differences** (`Src/Foo.cs` vs `src/foo.cs`, non-ASCII file names, CRLF and comments in JSON inputs, `.cmd` test commands such as `npm`/`dnx`) -> same file, unquoted names, parsed identically, resolved through `PATHEXT`. Tests: `TouchIndexTests.Overlap_IsCaseInsensitive`, `NonAsciiNames_AreUnquoted` (Task 10), `ConfigLoaderTests.CrlfAndComments_Accepted` (Task 5), `TasksFileTests.CrlfAndTrailingCommas_Accepted` (Task 9), `ProcessRunnerTests.Resolve_FindsCmdShimViaPathExt` (Task 3).

## File Structure

| Path | Responsibility |
|---|---|
| `src/Swarm.Tooling.props` | Shared build properties (net10.0, docs, warnings as errors, no source-revision in version) |
| `src/Swarm.Git/` | `ExitCodes`, `ToolException`, `ToolErrors`, `TextLines`, `SafeName`, `SharedFile`, `FileTree`, `ProcessRunner`, `GitRunner`, `RepoLocator` |
| `src/Swarm.RunState/` | `StatePaths`, `StateLayout`, `SwarmConfig`, `ConfigLoader`, `SwarmJson`, `OutputTypes`, `JsonlFile`, `EventLog`, `ReturnLedger`, `RunDirectories`, `Progress`, `SlotSemaphore`, `Cli/CliHost`, `Cli/CommonOptions` |
| `src/Swarm.Gate/GateRunner.cs` | Acquire slot, run command, release, `GateResult` |
| `src/Swarm.Batching/` | `TasksFile`, `TaskUnits`, `BatchPlanner`, `TouchIndex`, `IntegrationWorktree`, `RepoChecks`, `Landing` (`ILander`, `FastForwardLander`), `BatchEngine` |
| `src/Swarm.TestGate.Cli/` | `testgate run|status|reclaim` (package `Swarm.TestGate`, NOT REAL) |
| `src/Swarm.Batch.Cli/` | `batch run` (package `Swarm.Batch`, NOT REAL) |
| `tests/Swarm.FakeSuite/` | Tiny fake test command (green/red/interaction rules, sleep, child, orphan) |
| `tests/Swarm.Tools.Tests/` | xUnit tests; `Support/` fixtures (`TempRepo`, `TempDir`, `FakeSuite`, `TestConfig`) |
| `docs/batch-tools.md` | User reference for both tools |

---

### Task 1: Scaffold projects and `Swarm.Git` basics

**Files:**
- Create: `src/Swarm.Tooling.props`, `src/Swarm.Git/Swarm.Git.csproj`, `src/Swarm.RunState/Swarm.RunState.csproj`, `src/Swarm.Gate/Swarm.Gate.csproj`, `src/Swarm.Batching/Swarm.Batching.csproj`, `src/Swarm.TestGate.Cli/Swarm.TestGate.Cli.csproj`, `src/Swarm.TestGate.Cli/Program.cs`, `src/Swarm.Batch.Cli/Swarm.Batch.Cli.csproj`, `src/Swarm.Batch.Cli/Program.cs`, `tests/Swarm.Tools.Tests/Swarm.Tools.Tests.csproj`
- Create: `src/Swarm.Git/ExitCodes.cs`, `ToolException.cs`, `ToolErrors.cs`, `TextLines.cs`, `SafeName.cs`, `SharedFile.cs`, `FileTree.cs`
- Modify: `src/Swarm.sln` (add projects)
- Test: `tests/Swarm.Tools.Tests/Git/ToolErrorsTests.cs`, `tests/Swarm.Tools.Tests/Git/FileTreeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `Swarm.Git`):
  - `static class ExitCodes { const int Ok = 0, Returned = 1, Usage = 2, BadInput = 3, Environment = 4, GateTimeout = 5; }`
  - `sealed class ToolException(int exitCode, string message, string? hint = null) : Exception` with `int ExitCode`, `string? Hint`, `string Summary` (`<what> (<hint>)`), `string ErrorLine` (`error: ` + Summary), `static string Describe(string message, string? hint)`, `static string Format(string message, string? hint = null)`.
  - `static class ToolErrors { static int Handle(Func<int> body, TextWriter stderr); }` (ToolException -> its code; OperationCanceledException -> 4 `error: cancelled`; IO/Unauthorized -> 4; anything else -> 4 `error: unexpected failure: <Type>: <msg>`).
  - `static class TextLines { const int MaxOneLineLength = 400; static string OneLine(string? text); static IReadOnlyList<string> Split(string? text); }`
  - `static partial class SafeName { const string Description; const int MaxLength = 100; static bool IsValid(string? name); }` (letters, digits, `_`, `-`, single dots between segments, starts alphanumeric, not ending `.lock`).
  - `static class SharedFile { static void Retry(Action action, int attempts = 40, int delayMs = 25); static T Retry<T>(Func<T> action, int attempts = 40, int delayMs = 25); static bool IsTransient(Exception e); }`
  - `static class FileTree { static void DeleteTree(string directory); }` (clears read-only attributes, skips reparse points, retries).
  - Project graph: `Git <- RunState <- Gate <- Batching`; `TestGate.Cli -> Git, RunState, Gate`; `Batch.Cli -> all four`; `Tools.Tests -> all six` (+ FakeSuite build-only, Task 2).

- [ ] **Step 1: Write the shared props** (`src/Swarm.Tooling.props`)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the project files**

`src/Swarm.Git/Swarm.Git.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
</Project>
```

`src/Swarm.RunState/Swarm.RunState.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.Gate/Swarm.Gate.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.Batching/Swarm.Batching.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
    <ProjectReference Include="..\Swarm.Gate\Swarm.Gate.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.TestGate.Cli/Swarm.TestGate.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>true</IsPackable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>testgate</ToolCommandName>
    <PackageId>Swarm.TestGate</PackageId>
    <Version>0.1.0</Version>
    <Description>NOT REAL placeholder package id (unclaimed on nuget.org). Machine-wide test-slot gate for agent swarms.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
    <ProjectReference Include="..\Swarm.Gate\Swarm.Gate.csproj" />
  </ItemGroup>
</Project>
```

`src/Swarm.Batch.Cli/Swarm.Batch.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\Swarm.Tooling.props" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>true</IsPackable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>batch</ToolCommandName>
    <PackageId>Swarm.Batch</PackageId>
    <Version>0.1.0</Version>
    <Description>NOT REAL placeholder package id (unclaimed on nuget.org). Adaptive batched integration testing with bisect for agent swarms.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\Swarm.RunState\Swarm.RunState.csproj" />
    <ProjectReference Include="..\Swarm.Gate\Swarm.Gate.csproj" />
    <ProjectReference Include="..\Swarm.Batching\Swarm.Batching.csproj" />
  </ItemGroup>
</Project>
```

Placeholder entry points (replaced in Tasks 8 and 12), `src/Swarm.TestGate.Cli/Program.cs` and `src/Swarm.Batch.Cli/Program.cs` (change the namespace to `Swarm.Batch.Cli` in the second):

```csharp
namespace Swarm.TestGate.Cli;

/// <summary>Entry point (placeholder until the CLI task).</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Always 2 until implemented.</returns>
    public static int Main(string[] args) => 2;
}
```

`tests/Swarm.Tools.Tests/Swarm.Tools.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Swarm.Git\Swarm.Git.csproj" />
    <ProjectReference Include="..\..\src\Swarm.RunState\Swarm.RunState.csproj" />
    <ProjectReference Include="..\..\src\Swarm.Gate\Swarm.Gate.csproj" />
    <ProjectReference Include="..\..\src\Swarm.Batching\Swarm.Batching.csproj" />
    <ProjectReference Include="..\..\src\Swarm.TestGate.Cli\Swarm.TestGate.Cli.csproj" />
    <ProjectReference Include="..\..\src\Swarm.Batch.Cli\Swarm.Batch.Cli.csproj" />
  </ItemGroup>
</Project>
```

```bash
cd <repo-root>/src
dotnet sln Swarm.sln add Swarm.Git Swarm.RunState Swarm.Gate Swarm.Batching Swarm.TestGate.Cli Swarm.Batch.Cli ../tests/Swarm.Tools.Tests
```

- [ ] **Step 3: Write the failing tests**

`tests/Swarm.Tools.Tests/Git/ToolErrorsTests.cs`:

```csharp
using Swarm.Git;

namespace Swarm.Tools.Tests.Git;

public class ToolErrorsTests
{
    [Fact]
    public void ExitCodes_AreStable() =>
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[] { ExitCodes.Ok, ExitCodes.Returned, ExitCodes.Usage, ExitCodes.BadInput, ExitCodes.Environment, ExitCodes.GateTimeout });

    [Fact]
    public void ToolException_IsOneLineWithHint()
    {
        var e = new ToolException(ExitCodes.BadInput, "tasks file\r\nis   bad", "fix it");
        Assert.Equal("error: tasks file is bad (fix it)", e.ErrorLine);
        Assert.Equal("tasks file is bad (fix it)", e.Summary);
    }

    [Fact]
    public void Handle_MapsToolExceptionToItsCode()
    {
        var err = new StringWriter();
        var code = ToolErrors.Handle(() => throw new ToolException(ExitCodes.GateTimeout, "waited"), err);
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Equal("error: waited", err.ToString().TrimEnd());
    }

    [Fact]
    public void Handle_UnexpectedExceptionIsEnvironmentAndOneLine()
    {
        var err = new StringWriter();
        var code = ToolErrors.Handle(() => throw new InvalidOperationException("boom\nsecond line"), err);
        Assert.Equal(ExitCodes.Environment, code);
        Assert.Equal("error: unexpected failure: InvalidOperationException: boom second line", err.ToString().TrimEnd());
    }

    [Fact]
    public void Handle_CancelledIsEnvironment()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Environment, ToolErrors.Handle(() => throw new OperationCanceledException(), err));
        Assert.Equal("error: cancelled", err.ToString().TrimEnd());
    }

    [Fact]
    public void OneLine_TruncatesLongText() =>
        Assert.Equal(TextLines.MaxOneLineLength, TextLines.OneLine(new string('x', 1000)).Length);

    [Theory]
    [InlineData("E1", true)]
    [InlineData("T001", true)]
    [InlineData("feature.9933_x-y", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a..b", false)]
    [InlineData("a.", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("T1.lock", false)]
    public void SafeName_Rules(string name, bool ok) => Assert.Equal(ok, SafeName.IsValid(name));
}
```

`tests/Swarm.Tools.Tests/Git/FileTreeTests.cs`:

```csharp
using Swarm.Git;

namespace Swarm.Tools.Tests.Git;

public class FileTreeTests
{
    [Fact]
    public void DeletesReadOnlyFiles()
    {
        // Git object files are read-only on Windows; Directory.Delete alone fails on them.
        var dir = Path.Combine(Path.GetTempPath(), "swt-ft-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "objects", "ab", "cdef");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        FileTree.DeleteTree(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MissingDirectory_IsNoOp() => FileTree.DeleteTree(Path.Combine(Path.GetTempPath(), "swt-none-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Retry_SucceedsAfterTransientFailures()
    {
        var calls = 0;
        var value = SharedFile.Retry(() => ++calls < 3 ? throw new IOException("busy") : calls, attempts: 5, delayMs: 1);
        Assert.Equal(3, value);
    }

    [Fact]
    public void Retry_DoesNotRetryFileNotFound()
    {
        var calls = 0;
        Assert.Throws<FileNotFoundException>(() => SharedFile.Retry(() => { calls++; throw new FileNotFoundException(); }, attempts: 5, delayMs: 1));
        Assert.Equal(1, calls);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror`
Expected: FAIL to compile (`ExitCodes`, `ToolException`, ... not defined).

- [ ] **Step 5: Implement `Swarm.Git` basics**

`src/Swarm.Git/ExitCodes.cs`:

```csharp
namespace Swarm.Git;

/// <summary>
/// Process exit codes shared by every swarm tool. Schema version 1: values are never renumbered, only appended.
/// </summary>
public static class ExitCodes
{
    /// <summary>Everything succeeded (batch: every task landed, including after an automatic rebase).</summary>
    public const int Ok = 0;

    /// <summary>The tool worked but work came back: tasks returned (batch) or the gated command failed (testgate).</summary>
    public const int Returned = 1;

    /// <summary>Usage or configuration error, including over-long paths.</summary>
    public const int Usage = 2;

    /// <summary>Bad input: tasks file unreadable or invalid, epic branch missing or checked out.</summary>
    public const int BadInput = 3;

    /// <summary>Environment failure: git, worktree, file system, a command that cannot start, a lander contract violation, cancellation.</summary>
    public const int Environment = 4;

    /// <summary>No test slot became free within the configured maximum wait.</summary>
    public const int GateTimeout = 5;
}
```

`src/Swarm.Git/ToolException.cs`:

```csharp
namespace Swarm.Git;

/// <summary>A failure a swarm tool reports as one stderr line and a specific exit code.</summary>
public sealed class ToolException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ToolException"/> class.</summary>
    /// <param name="exitCode">The exit code to use (see <see cref="ExitCodes"/>).</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="hint">Optional short advice, printed in parentheses.</param>
    public ToolException(int exitCode, string message, string? hint = null)
        : base(message)
    {
        ExitCode = exitCode;
        Hint = hint;
    }

    /// <summary>Gets the exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the optional hint.</summary>
    public string? Hint { get; }

    /// <summary>Gets the one-line description: <c>&lt;what&gt; (&lt;hint&gt;)</c>.</summary>
    public string Summary => Describe(Message, Hint);

    /// <summary>Gets the stderr line: <c>error: &lt;what&gt; (&lt;hint&gt;)</c>.</summary>
    public string ErrorLine => "error: " + Summary;

    /// <summary>Builds the one-line description.</summary>
    /// <param name="message">What went wrong; line breaks are collapsed.</param>
    /// <param name="hint">Optional advice.</param>
    /// <returns>The description without the <c>error: </c> prefix.</returns>
    public static string Describe(string message, string? hint)
    {
        var what = TextLines.OneLine(message);
        return string.IsNullOrWhiteSpace(hint) ? what : $"{what} ({TextLines.OneLine(hint)})";
    }

    /// <summary>Formats a one-line error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="hint">Optional advice.</param>
    /// <returns>The formatted line, without a trailing newline.</returns>
    public static string Format(string message, string? hint = null) => "error: " + Describe(message, hint);
}
```

`src/Swarm.Git/ToolErrors.cs`:

```csharp
namespace Swarm.Git;

/// <summary>Turns any failure into one stderr line and an exit code.</summary>
public static class ToolErrors
{
    /// <summary>Runs a command body and maps failures to exit codes.</summary>
    /// <param name="body">The command body; returns the exit code on success.</param>
    /// <param name="stderr">Where the single error line goes.</param>
    /// <returns>The body's exit code, or the code for the failure.</returns>
    public static int Handle(Func<int> body, TextWriter stderr)
    {
        try
        {
            return body();
        }
        catch (ToolException e)
        {
            stderr.WriteLine(e.ErrorLine);
            return e.ExitCode;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(ToolException.Format("cancelled"));
            return ExitCodes.Environment;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine(ToolException.Format(e.Message, "file system error"));
            return ExitCodes.Environment;
        }
        catch (Exception e)
        {
            stderr.WriteLine(ToolException.Format($"unexpected failure: {e.GetType().Name}: {e.Message}"));
            return ExitCodes.Environment;
        }
    }
}
```

`src/Swarm.Git/TextLines.cs`:

```csharp
namespace Swarm.Git;

/// <summary>Small text helpers for one-line messages and line lists.</summary>
public static class TextLines
{
    /// <summary>Maximum length of a one-line message.</summary>
    public const int MaxOneLineLength = 400;

    static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    /// <summary>Collapses all whitespace runs (including line breaks) to single spaces and truncates.</summary>
    /// <param name="text">Any text.</param>
    /// <returns>A single line of at most <see cref="MaxOneLineLength"/> characters.</returns>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var collapsed = string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxOneLineLength ? collapsed : string.Concat(collapsed.AsSpan(0, MaxOneLineLength - 3), "...");
    }

    /// <summary>Splits command output into trimmed, non-empty lines.</summary>
    /// <param name="text">Output text (LF or CRLF).</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
```

`src/Swarm.Git/SafeName.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Swarm.Git;

/// <summary>Names that are safe as file names, ref components and run ids (epic ids, task ids, run ids).</summary>
public static partial class SafeName
{
    /// <summary>Human description used in error messages.</summary>
    public const string Description = "letters, digits, '_', '-' and single dots, starting with a letter or digit, not ending in '.lock', at most 100 chars";

    /// <summary>Maximum length.</summary>
    public const int MaxLength = 100;

    /// <summary>Checks a name.</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>True when the name is safe.</returns>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && Matcher().IsMatch(name)
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Matcher();
}
```

`src/Swarm.Git/SharedFile.cs`:

```csharp
namespace Swarm.Git;

/// <summary>Retries file operations that fail transiently on Windows (sharing violations, delete-pending, scanners).</summary>
public static class SharedFile
{
    /// <summary>Runs an action, retrying transient file failures.</summary>
    /// <param name="action">The file operation.</param>
    /// <param name="attempts">Total attempts.</param>
    /// <param name="delayMs">Delay between attempts.</param>
    public static void Retry(Action action, int attempts = 40, int delayMs = 25) =>
        Retry(() => { action(); return true; }, attempts, delayMs);

    /// <summary>Runs a function, retrying transient file failures.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The file operation.</param>
    /// <param name="attempts">Total attempts.</param>
    /// <param name="delayMs">Delay between attempts.</param>
    /// <returns>The function's result.</returns>
    public static T Retry<T>(Func<T> action, int attempts = 40, int delayMs = 25)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (attempt < attempts && IsTransient(e))
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Decides whether a failure is worth retrying.</summary>
    /// <param name="e">The failure.</param>
    /// <returns>True for sharing/lock/access failures; false for missing files or directories.</returns>
    public static bool IsTransient(Exception e) =>
        e is (IOException and not FileNotFoundException and not DirectoryNotFoundException) or UnauthorizedAccessException;
}
```

`src/Swarm.Git/FileTree.cs`:

```csharp
namespace Swarm.Git;

/// <summary>Deletes directory trees the way Windows needs.</summary>
public static class FileTree
{
    /// <summary>Deletes a directory tree, clearing read-only attributes (git objects) first; a missing directory is a no-op.</summary>
    /// <param name="directory">The directory.</param>
    public static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Skip reparse points so a junction never leads the attribute reset outside the tree.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        SharedFile.Retry(() => Directory.Delete(directory, recursive: true));
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet build src/Swarm.sln -warnaserror && dotnet test tests/Swarm.Tools.Tests -warnaserror`
Expected: build 0 warnings 0 errors; all `ToolErrorsTests` and `FileTreeTests` PASS; the existing `tests/Swarm.Tests` still build.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Scaffold testgate/batch projects and Swarm.Git basics" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 2: Test fixtures: FakeSuite, TempDir, TempRepo

**Files:**
- Create: `tests/Swarm.FakeSuite/Swarm.FakeSuite.csproj`, `tests/Swarm.FakeSuite/Program.cs`
- Create: `tests/Swarm.Tools.Tests/Support/TestPaths.cs`, `Support/TempRepo.cs`, `Support/FakeSuite.cs`
- Modify: `tests/Swarm.Tools.Tests/Swarm.Tools.Tests.csproj` (build-only reference to FakeSuite), `src/Swarm.sln`
- Test: `tests/Swarm.Tools.Tests/Support/FixtureTests.cs`

**Interfaces:**
- Consumes: `FileTree.DeleteTree` (Task 1).
- Produces (namespace `Swarm.Tools.Tests.Support`, test-only):
  - FakeSuite command semantics, run in a working directory `cwd`: prints `fake-suite args: <args>`; for each top-level `*.fail` file in `cwd` (ordinal order) whose non-empty lines (file names relative to `cwd`) ALL exist, prints `fake-suite: red (<name>.fail)` and exits 1 (an empty `.fail` file is always red); otherwise prints `fake-suite: green` and exits 0. Options: `--sleep-ms N` (sleep before the verdict); `--child-sleep-ms N` (spawn a child FakeSuite `--sleep-ms N`, wait for it); `--orphan-ms N` (spawn such a child that inherits stdout/stderr, print green, exit 0 at once); `--pid-file P` (write the spawned child's pid).
  - `static class FakeSuite { static string DllPath { get; } static IReadOnlyList<string> Command(params string[] extra); }` -> `["dotnet", DllPath, ..extra]`.
  - `static class TestPaths { static string Root { get; } static string NewSandbox(); }` (`SWARM_TEST_ROOT` or `%TEMP%\swt`; sandbox = `<Root>\<8 hex>`).
  - `sealed class TempDir : IDisposable { string Dir { get; } }`
  - `sealed record TaskLine(string Id, string Branch, string[]? DependsOn = null)`
  - `sealed class TempRepo : IDisposable` with `static TempRepo Create()`, `string Sandbox`, `string Root` (git's `--show-toplevel`, full path), `string StateDir` (`<Sandbox>\state`), `string WorktreeRoot` (`<Sandbox>\wt`), `string Git(params string[] args)`, `static string RunGit(string dir, params string[] args)`, `void Write(string relativePath, string content)`, `string Commit(string message, params (string Path, string Content)[] files)`, `string Branch(string name, string from, params (string Path, string Content)[] files)`, `void Epic(string from = "main", string name = "epic/E1")`, `string Sha(string rev)`, `bool HasFile(string rev, string path)`, `string WriteTasks(params TaskLine[] tasks)` (writes `<Sandbox>\tasks.json`, returns its path). Repo: `main` branch, local identity, `core.autocrlf=false`, initial commit with `README.md`.

- [ ] **Step 1: Write the fake suite**

`tests/Swarm.FakeSuite/Swarm.FakeSuite.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`tests/Swarm.FakeSuite/Program.cs`:

```csharp
// Fake test suite for swarm tool tests: verdict from *.fail rule files in the working directory, plus
// options to sleep, spawn a waited-for child, or leave an orphan child that holds the output pipes.
using System.Diagnostics;
using System.Globalization;

var options = ParseOptions(args);
Console.WriteLine("fake-suite args: " + string.Join(' ', args));

if (options.TryGetValue("--orphan-ms", out var orphanMs))
{
    var orphan = Spawn(orphanMs);
    WritePid(options, orphan.Id);
    Console.WriteLine("fake-suite: green");
    return 0;
}

if (options.TryGetValue("--child-sleep-ms", out var childMs))
{
    using var child = Spawn(childMs);
    WritePid(options, child.Id);
    child.WaitForExit();
}

if (options.TryGetValue("--sleep-ms", out var sleepMs))
{
    Thread.Sleep(int.Parse(sleepMs, CultureInfo.InvariantCulture));
}

var cwd = Directory.GetCurrentDirectory();
foreach (var rule in Directory.EnumerateFiles(cwd, "*.fail").Order(StringComparer.Ordinal))
{
    var required = File.ReadAllLines(rule).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    if (required.All(r => File.Exists(Path.Combine(cwd, r))))
    {
        Console.WriteLine($"fake-suite: red ({Path.GetFileName(rule)})");
        return 1;
    }
}

Console.WriteLine("fake-suite: green");
return 0;

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal) && !a[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            d[a[i]] = a[++i];
        }
    }

    return d;
}

// The child inherits this process's std handles (no redirection), so an orphan keeps the caller's pipes open.
static Process Spawn(string sleepMs)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    psi.ArgumentList.Add("--sleep-ms");
    psi.ArgumentList.Add(sleepMs);
    return Process.Start(psi)!;
}

static void WritePid(Dictionary<string, string> o, int pid)
{
    if (o.TryGetValue("--pid-file", out var f))
    {
        File.WriteAllText(f, pid.ToString(CultureInfo.InvariantCulture));
    }
}
```

Add to the test csproj's project-reference `ItemGroup` (forces build order without referencing the assembly):

```xml
    <ProjectReference Include="..\Swarm.FakeSuite\Swarm.FakeSuite.csproj" ReferenceOutputAssembly="false" />
```

```bash
cd <repo-root>/src && dotnet sln Swarm.sln add ../tests/Swarm.FakeSuite
```

- [ ] **Step 2: Write the failing fixture tests** (`tests/Swarm.Tools.Tests/Support/FixtureTests.cs`)

```csharp
using System.Diagnostics;

namespace Swarm.Tools.Tests.Support;

public class FixtureTests
{
    static (int Code, string Out) RunFake(string cwd, params string[] extra)
    {
        var cmd = FakeSuite.Command(extra);
        var psi = new ProcessStartInfo(cmd[0]) { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in cmd.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void FakeSuite_GreenByDefault()
    {
        using var dir = new TempDir();
        var (code, output) = RunFake(dir.Dir);
        Assert.Equal(0, code);
        Assert.Contains("fake-suite: green", output);
    }

    [Fact]
    public void FakeSuite_EmptyFailFileIsRed()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "T3.fail"), "");
        var (code, output) = RunFake(dir.Dir);
        Assert.Equal(1, code);
        Assert.Contains("fake-suite: red (T3.fail)", output);
    }

    [Fact]
    public void FakeSuite_InteractionRuleNeedsAllFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "pair.fail"), "t3.txt\n");
        Assert.Equal(0, RunFake(dir.Dir).Code);
        File.WriteAllText(Path.Combine(dir.Dir, "t3.txt"), "x");
        Assert.Equal(1, RunFake(dir.Dir).Code);
    }

    [Fact]
    public void TempRepo_BranchIsNotOnMain()
    {
        using var repo = TempRepo.Create();
        repo.Branch("task/T1", "main", ("one.txt", "one\n"));
        Assert.True(repo.HasFile("task/T1", "one.txt"));
        Assert.False(repo.HasFile("main", "one.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "one.txt")));
    }

    [Fact]
    public void TempRepo_WriteTasksIsCamelCaseJson()
    {
        using var repo = TempRepo.Create();
        var text = File.ReadAllText(repo.WriteTasks(new TaskLine("T2", "task/T2", ["T1"])));
        Assert.Contains("\"dependsOn\":[\"T1\"]", text);
    }

    [Fact]
    public void TempRepo_DisposeDeletesSandboxIncludingGitObjects()
    {
        var repo = TempRepo.Create();
        var sandbox = repo.Sandbox;
        repo.Dispose();
        Assert.False(Directory.Exists(sandbox));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~FixtureTests`
Expected: FAIL to compile (`FakeSuite`, `TempDir`, `TempRepo` not defined).

- [ ] **Step 4: Implement the support classes**

`tests/Swarm.Tools.Tests/Support/TestPaths.cs`:

```csharp
using Swarm.Git;

namespace Swarm.Tools.Tests.Support;

/// <summary>Short sandbox roots, far below the 200-char path guard.</summary>
public static class TestPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("SWARM_TEST_ROOT") is { Length: > 0 } r ? r : Path.Combine(Path.GetTempPath(), "swt");

    public static string NewSandbox()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }
}

public sealed class TempDir : IDisposable
{
    public string Dir { get; } = TestPaths.NewSandbox();

    public void Dispose()
    {
        try
        {
            FileTree.DeleteTree(Dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A lingering child process may still hold a file; the temp root is disposable.
        }
    }
}
```

`tests/Swarm.Tools.Tests/Support/FakeSuite.cs`:

```csharp
namespace Swarm.Tools.Tests.Support;

public static class FakeSuite
{
    public static string DllPath { get; } = Locate();

    public static IReadOnlyList<string> Command(params string[] extra) => ["dotnet", DllPath, .. extra];

    // tests/Swarm.Tools.Tests/bin/<Configuration>/<tfm>/ -> tests/Swarm.FakeSuite/bin/<Configuration>/<tfm>/Swarm.FakeSuite.dll
    static string Locate()
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        var configuration = tfmDir.Parent!.Name;
        var testsDir = tfmDir.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(testsDir, "Swarm.FakeSuite", "bin", configuration, tfmDir.Name, "Swarm.FakeSuite.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException($"FakeSuite not built at {path}; build tests/Swarm.FakeSuite first", path);
    }
}
```

`tests/Swarm.Tools.Tests/Support/TempRepo.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Swarm.Git;

namespace Swarm.Tools.Tests.Support;

public sealed record TaskLine(string Id, string Branch, string[]? DependsOn = null);

/// <summary>A throwaway git repo in its own sandbox (sandbox/repo, sandbox/state, sandbox/wt).</summary>
public sealed class TempRepo : IDisposable
{
    TempRepo(string sandbox)
    {
        Sandbox = sandbox;
        var dir = Path.Combine(sandbox, "repo");
        Directory.CreateDirectory(dir);
        RunGit(dir, "init", "-q", "-b", "main");
        RunGit(dir, "config", "user.name", "test");
        RunGit(dir, "config", "user.email", "test@example.invalid");
        RunGit(dir, "config", "core.autocrlf", "false");
        Root = Path.GetFullPath(RunGit(dir, "rev-parse", "--show-toplevel"));
        Commit("init", ("README.md", "readme\n"));
    }

    public string Sandbox { get; }

    public string Root { get; }

    public string StateDir => Path.Combine(Sandbox, "state");

    public string WorktreeRoot => Path.Combine(Sandbox, "wt");

    public static TempRepo Create() => new(TestPaths.NewSandbox());

    public static string RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])["-c", "core.autocrlf=false", "-c", "core.quotepath=false", .. args])
        {
            psi.ArgumentList.Add(a);
        }

        var errors = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}: {errors}");
    }

    public string Git(params string[] args) => RunGit(Root, args);

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
    }

    public string Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            Write(path, content);
        }

        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
        return Sha("HEAD");
    }

    public string Branch(string name, string from, params (string Path, string Content)[] files)
    {
        Git("checkout", "-q", "-b", name, from);
        try
        {
            return Commit($"{name}: change", files);
        }
        finally
        {
            Git("checkout", "-q", "main");
        }
    }

    public void Epic(string from = "main", string name = "epic/E1") => Git("branch", name, from);

    public string Sha(string rev) => Git("rev-parse", rev);

    public bool HasFile(string rev, string path)
    {
        try
        {
            Git("cat-file", "-e", $"{rev}:{path}");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public string WriteTasks(params TaskLine[] tasks)
    {
        var path = Path.Combine(Sandbox, "tasks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(tasks.Select(t => new { id = t.Id, branch = t.Branch, dependsOn = t.DependsOn ?? Array.Empty<string>() })));
        return path;
    }

    public void Dispose()
    {
        try
        {
            FileTree.DeleteTree(Sandbox);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An orphaned FakeSuite child may still hold its cwd; the temp root is disposable.
        }
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~FixtureTests`
Expected: 6 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Swarm.sln tests
git commit -m "Add FakeSuite and temp-repo test fixtures" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 3: Process runner and git runner

**Files:**
- Create: `src/Swarm.Git/ProcessRunner.cs`, `src/Swarm.Git/GitRunner.cs`
- Test: `tests/Swarm.Tools.Tests/Git/ProcessRunnerTests.cs`, `tests/Swarm.Tools.Tests/Git/GitRunnerTests.cs`

**Interfaces:**
- Consumes: `ToolException`, `ExitCodes`, `TextLines` (Task 1); `FakeSuite`, `TempDir`, `TempRepo` (Task 2).
- Produces (namespace `Swarm.Git`):
  - `sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool Killed)` (`ExitCode` is `-1` when `Killed`).
  - `sealed record ProcessRunOptions { TimeSpan? Timeout; Action<string>? OnLine; IReadOnlyDictionary<string, string?>? EnvironmentVariables; TimeSpan OutputGrace = 5 s; }` (`OnLine` receives every stdout and stderr line, serialised under one lock).
  - `static class ProcessRunner { static string Resolve(string fileName, string? workingDirectory = null, string? pathEnv = null, string? pathExt = null); static ProcessResult Run(string fileName, IReadOnlyList<string> args, string workingDirectory, ProcessRunOptions? options = null, CancellationToken cancellationToken = default); }` — stdin closed; timeout -> whole tree killed, `Killed = true`; cancellation -> whole tree killed, `OperationCanceledException`; start failure -> `ToolException(Environment, "cannot start '<file>': ...")`; after exit waits at most `OutputGrace` for output EOF (a grandchild may hold the pipes).
  - `sealed class GitRunner` with `GitRunner(string workingDirectory)`, `string WorkingDirectory`, `static IReadOnlyList<string> BaseConfig` (`core.autocrlf=false`, `core.longpaths=true`, `core.quotepath=false`, `advice.detachedHead=false`), `static IReadOnlyList<string> ToolIdentity` (`user.name=swarm-batch`, `user.email=swarm-batch@example.invalid`), `GitRunner WithIdentity()`, `GitRunner At(string workingDirectory)`, `ProcessResult Try(params string[] args)`, `string Run(params string[] args)` (trimmed stdout; non-zero -> `ToolException(Environment, "git <args> failed in <dir>: <one-line stderr>")`), `IReadOnlyList<string> Lines(params string[] args)`, `bool RefExists(string fullRef)`, `string RevParse(string rev)` (full sha of `rev^{commit}`). Environment: `GIT_TERMINAL_PROMPT=0`, `GIT_EDITOR=true`, `GIT_MERGE_AUTOEDIT=no`.

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/Git/ProcessRunnerTests.cs`:

```csharp
using System.Diagnostics;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Git;

public class ProcessRunnerTests
{
    static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static bool GoneWithin(int pid, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (IsRunning(pid))
        {
            if (sw.Elapsed > limit)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    static ProcessResult Fake(string cwd, ProcessRunOptions? options = null, CancellationToken ct = default, params string[] extra)
    {
        var cmd = FakeSuite.Command(extra);
        return ProcessRunner.Run(cmd[0], cmd.Skip(1).ToList(), cwd, options, ct);
    }

    [Fact]
    public void Run_CapturesOutputLinesAndExitCode()
    {
        using var dir = new TempDir();
        var lines = new List<string>();
        var r = Fake(dir.Dir, new ProcessRunOptions { OnLine = lines.Add });
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.Killed);
        Assert.Contains("fake-suite: green", r.StdOut);
        Assert.Contains("fake-suite: green", lines);
    }

    [Fact]
    public void Run_MissingCommand_IsOneLineEnvironmentError()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ToolException>(() => ProcessRunner.Run("definitely-not-a-command-xyz", [], dir.Dir));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.StartsWith("error: cannot start 'definitely-not-a-command-xyz'", e.ErrorLine);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void Timeout_KillsWholeTree()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "child.pid");
        var r = Fake(dir.Dir, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(3) }, default, "--child-sleep-ms", "60000", "--pid-file", pidFile);
        Assert.True(r.Killed);
        Assert.Equal(-1, r.ExitCode);
        var child = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(GoneWithin(child, TimeSpan.FromSeconds(5)), "grandchild survived the tree kill");
    }

    [Fact]
    public void Cancel_KillsTreeAndThrows()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "child.pid");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Throws<OperationCanceledException>(() => Fake(dir.Dir, null, cts.Token, "--child-sleep-ms", "60000", "--pid-file", pidFile));
        var child = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(GoneWithin(child, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OrphanHoldingPipes_DoesNotHang()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Dir, "orphan.pid");
        var sw = Stopwatch.StartNew();
        try
        {
            var r = Fake(dir.Dir, new ProcessRunOptions { OutputGrace = TimeSpan.FromSeconds(1) }, default, "--orphan-ms", "20000", "--pid-file", pidFile);
            Assert.Equal(0, r.ExitCode);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                Kill(int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [Fact]
    public void Resolve_FindsCmdShimViaPathExt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var shim = Path.Combine(dir.Dir, "hello.cmd");
        File.WriteAllText(shim, "@echo hi\r\n");
        Assert.Equal(shim, ProcessRunner.Resolve("hello", null, dir.Dir, ".EXE;.CMD"), ignoreCase: true);
        Assert.Contains("hi", ProcessRunner.Run(ProcessRunner.Resolve("hello", null, dir.Dir, ".EXE;.CMD"), [], dir.Dir).StdOut);
    }

    [Fact]
    public void Resolve_RelativePathIsAgainstWorkingDirectory()
    {
        using var dir = new TempDir();
        Assert.Equal(Path.Combine(dir.Dir, "tools", "x.exe"), ProcessRunner.Resolve("tools/x.exe", dir.Dir));
    }
}
```

`tests/Swarm.Tools.Tests/Git/GitRunnerTests.cs`:

```csharp
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Git;

public class GitRunnerTests
{
    [Fact]
    public void Failure_IsOneLineEnvironmentError()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => new GitRunner(repo.Root).Run("checkout", "no-such-branch"));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("git checkout no-such-branch failed in", e.ErrorLine);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void ForcesAutocrlfFalseOverRepoConfig()
    {
        using var repo = TempRepo.Create();
        repo.Git("config", "core.autocrlf", "true");
        Assert.Equal("false", new GitRunner(repo.Root).Run("config", "--get", "core.autocrlf"));
    }

    [Fact]
    public void RefExistsAndRevParse()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root);
        Assert.True(git.RefExists("refs/heads/main"));
        Assert.False(git.RefExists("refs/heads/nope"));
        Assert.Equal(repo.Sha("main"), git.RevParse("refs/heads/main"));
    }

    [Fact]
    public void WithIdentity_CommitsAsTool()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root).WithIdentity();
        git.Run("commit", "-q", "--allow-empty", "-m", "tool commit");
        Assert.Equal("swarm-batch", git.Run("log", "-1", "--format=%an"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~ProcessRunnerTests|FullyQualifiedName~GitRunnerTests"`
Expected: FAIL to compile (`ProcessRunner`, `GitRunner` not defined).

- [ ] **Step 3: Implement `ProcessRunner`** (`src/Swarm.Git/ProcessRunner.cs`)

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Swarm.Git;

/// <summary>Result of a finished (or killed) process.</summary>
/// <param name="ExitCode">The exit code, or -1 when the process was killed.</param>
/// <param name="StdOut">Captured stdout, LF-separated.</param>
/// <param name="StdErr">Captured stderr, LF-separated.</param>
/// <param name="Killed">True when the process tree was killed by a timeout.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool Killed);

/// <summary>Options for <see cref="ProcessRunner.Run"/>.</summary>
public sealed record ProcessRunOptions
{
    /// <summary>Gets the run time limit; the whole process tree is killed when exceeded.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets a callback for every stdout and stderr line (called under one lock, keep it cheap).</summary>
    public Action<string>? OnLine { get; init; }

    /// <summary>Gets extra environment variables (null value removes the variable).</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>Gets how long to wait for output EOF after exit (a grandchild may still hold the pipes).</summary>
    public TimeSpan OutputGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Runs child processes with closed stdin, captured output, and whole-tree kill.</summary>
public static class ProcessRunner
{
    /// <summary>Resolves a command name to a path. On Windows, bare names are searched on PATH with PATHEXT, so <c>npm</c> finds <c>npm.cmd</c>.</summary>
    /// <param name="fileName">A bare name, a relative path or an absolute path.</param>
    /// <param name="workingDirectory">Base for relative paths (default: current directory).</param>
    /// <param name="pathEnv">PATH override (tests).</param>
    /// <param name="pathExt">PATHEXT override (tests).</param>
    /// <returns>The resolved path, or <paramref name="fileName"/> unchanged when nothing was found.</returns>
    public static string Resolve(string fileName, string? workingDirectory = null, string? pathEnv = null, string? pathExt = null)
    {
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        if (fileName.Contains('/') || fileName.Contains('\\'))
        {
            return Path.GetFullPath(Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), fileName));
        }

        if (!OperatingSystem.IsWindows())
        {
            return fileName;
        }

        var extensions = Path.HasExtension(fileName)
            ? new[] { "" }
            : (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in (pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, fileName + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    /// <summary>Runs a process to completion.</summary>
    /// <param name="fileName">Command (resolved with <see cref="Resolve"/>).</param>
    /// <param name="args">Arguments, passed without shell interpretation.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="options">Timeout, line callback, environment, output grace.</param>
    /// <param name="cancellationToken">Cancels the run: the whole tree is killed first.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">The process cannot be started (exit code 4).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static ProcessResult Run(string fileName, IReadOnlyList<string> args, string workingDirectory, ProcessRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ProcessRunOptions();
        var psi = new ProcessStartInfo(Resolve(fileName, workingDirectory))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (key, value) in options.EnvironmentVariables ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        var gate = new object();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Collect(string? line, StringBuilder sink, TaskCompletionSource done)
        {
            if (line is null)
            {
                done.TrySetResult();
                return;
            }

            lock (gate)
            {
                sink.Append(line).Append('\n');
                options.OnLine?.Invoke(line);
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, outDone);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, errDone);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ToolException(ExitCodes.Environment, $"cannot start '{fileName}': {e.Message}", "check the command exists on PATH");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        var killed = false;

        // Poll instead of WaitForExit(): the parameterless overload also waits for pipe EOF, which a
        // grandchild (e.g. an MSBuild node) can hold open forever.
        while (!process.WaitForExit(100))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                KillTree(process);
                process.WaitForExit(5000);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (options.Timeout is { } limit && clock.Elapsed > limit)
            {
                KillTree(process);
                killed = true;
                process.WaitForExit(5000);
                break;
            }
        }

        Task.WaitAll([outDone.Task, errDone.Task], options.OutputGrace);
        lock (gate)
        {
            return new ProcessResult(killed ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), killed);
        }
    }

    static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }
}
```

- [ ] **Step 4: Implement `GitRunner`** (`src/Swarm.Git/GitRunner.cs`)

```csharp
namespace Swarm.Git;

/// <summary>Runs git with fixed, platform-safe configuration and one-line failures.</summary>
public sealed class GitRunner
{
    static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_EDITOR"] = "true",
        ["GIT_MERGE_AUTOEDIT"] = "no",
    };

    readonly IReadOnlyList<string> extraConfig;

    /// <summary>Initializes a new instance of the <see cref="GitRunner"/> class.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    public GitRunner(string workingDirectory)
        : this(workingDirectory, [])
    {
    }

    GitRunner(string workingDirectory, IReadOnlyList<string> extraConfig)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        this.extraConfig = extraConfig;
    }

    /// <summary>Gets the configuration passed to every git call (no CRLF conversion, long paths, unquoted names).</summary>
    public static IReadOnlyList<string> BaseConfig { get; } =
        ["-c", "core.autocrlf=false", "-c", "core.longpaths=true", "-c", "core.quotepath=false", "-c", "advice.detachedHead=false"];

    /// <summary>Gets the committer identity used for tool-made commits (integration merges, rebased copies).</summary>
    public static IReadOnlyList<string> ToolIdentity { get; } =
        ["-c", "user.name=swarm-batch", "-c", "user.email=swarm-batch@example.invalid"];

    /// <summary>Gets the working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Returns a runner that commits with <see cref="ToolIdentity"/>.</summary>
    /// <returns>The new runner.</returns>
    public GitRunner WithIdentity() => new(WorkingDirectory, ToolIdentity);

    /// <summary>Returns a runner with the same configuration in another directory.</summary>
    /// <param name="workingDirectory">The directory.</param>
    /// <returns>The new runner.</returns>
    public GitRunner At(string workingDirectory) => new(workingDirectory, extraConfig);

    /// <summary>Runs git and returns the raw result.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>The result, whatever the exit code.</returns>
    public ProcessResult Try(params string[] args) =>
        ProcessRunner.Run("git", [.. BaseConfig, .. extraConfig, .. args], WorkingDirectory, new ProcessRunOptions { EnvironmentVariables = Env });

    /// <summary>Runs git and requires success.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>Trimmed stdout.</returns>
    /// <exception cref="ToolException">Git failed (exit code 4).</exception>
    public string Run(params string[] args)
    {
        var r = Try(args);
        if (r.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Environment, $"git {string.Join(' ', args)} failed in {WorkingDirectory}: {TextLines.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut)}");
        }

        return r.StdOut.Trim();
    }

    /// <summary>Runs git and splits stdout into lines.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>Non-empty trimmed lines.</returns>
    public IReadOnlyList<string> Lines(params string[] args) => TextLines.Split(Run(args));

    /// <summary>Checks that a ref names a commit.</summary>
    /// <param name="fullRef">For example <c>refs/heads/task/T1</c>.</param>
    /// <returns>True when it exists.</returns>
    public bool RefExists(string fullRef) => Try("rev-parse", "--verify", "--quiet", fullRef + "^{commit}").ExitCode == 0;

    /// <summary>Resolves a revision to a full commit sha.</summary>
    /// <param name="rev">The revision.</param>
    /// <returns>The sha.</returns>
    public string RevParse(string rev) => Run("rev-parse", "--verify", rev + "^{commit}");
}
```

- [ ] **Step 5: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~ProcessRunnerTests|FullyQualifiedName~GitRunnerTests"`
Expected: all PASS (Windows-only `Resolve_FindsCmdShimViaPathExt` returns early elsewhere).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "Add process runner with tree kill and git runner" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 4: Repository, main-worktree and state-dir resolution

**Files:**
- Create: `src/Swarm.Git/RepoLocator.cs`, `src/Swarm.RunState/StatePaths.cs`
- Test: `tests/Swarm.Tools.Tests/RunState/StatePathsTests.cs`

**Interfaces:**
- Consumes: `GitRunner`, `ToolException` (Tasks 1, 3); `TempRepo` (Task 2).
- Produces:
  - `Swarm.Git.RepoPaths(string WorktreeRoot, string MainWorktreeRoot, string CommonGitDir)` record.
  - `Swarm.Git.RepoLocator.Locate(string startDirectory) -> RepoPaths` (main worktree = parent of `git rev-parse --path-format=absolute --git-common-dir` when it ends in `.git`; not a repo -> `ToolException(BadInput)`; bare / `--separate-git-dir` -> `ToolException(BadInput)`).
  - `Swarm.RunState.StatePaths { const int MaxPathLength = 200; static string Resolve(RepoPaths repo, string configured); static string ResolveWorktreeRoot(RepoPaths repo, string? configured); static string Guard(string fullPath, string what); }` — relative state dirs resolve under the **main** worktree; `Guard` throws `ToolException(Usage, "<what> path is <n> chars (limit 200)", "use a shorter path; Windows MAX_PATH breaks git and dotnet children")`; default worktree root `<parent of main>/<main name>-wt`.
  - `Swarm.RunState.StateLayout(string Root)` record with `SlotsDir` (`<root>/slots`), `RunsDir` (`<root>/runs`), `GateEventsFile` (`<root>/testgate.events.jsonl`), `BatchLockDir(string epic)` (`<root>/locks/batch-<epic>`), `RunDir(string runId)` (`<root>/runs/<runId>`).

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/RunState/StatePathsTests.cs`)

```csharp
using System.Text.RegularExpressions;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class StatePathsTests
{
    static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~StatePathsTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `RepoLocator`** (`src/Swarm.Git/RepoLocator.cs`)

```csharp
namespace Swarm.Git;

/// <summary>Where a repository lives on disk.</summary>
/// <param name="WorktreeRoot">Root of the worktree containing the start directory.</param>
/// <param name="MainWorktreeRoot">Root of the main worktree (shared run state and config live here).</param>
/// <param name="CommonGitDir">The repository's common git directory.</param>
public sealed record RepoPaths(string WorktreeRoot, string MainWorktreeRoot, string CommonGitDir);

/// <summary>Finds the current and main worktree of a repository.</summary>
public static class RepoLocator
{
    /// <summary>Locates the repository containing a directory.</summary>
    /// <param name="startDirectory">Any directory inside a worktree.</param>
    /// <returns>The paths.</returns>
    /// <exception cref="ToolException">Not inside a worktree, or the repository has no main worktree (exit code 3).</exception>
    public static RepoPaths Locate(string startDirectory)
    {
        if (!Directory.Exists(startDirectory))
        {
            throw new ToolException(ExitCodes.BadInput, $"directory '{startDirectory}' does not exist");
        }

        var git = new GitRunner(startDirectory);
        var top = git.Try("rev-parse", "--show-toplevel");
        if (top.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.BadInput, $"'{startDirectory}' is not inside a git worktree", "run from a clone or one of its worktrees");
        }

        // All worktrees share one common dir; for a normal clone it is <main worktree>/.git.
        var common = Path.GetFullPath(git.Run("rev-parse", "--path-format=absolute", "--git-common-dir")).TrimEnd('\\', '/');
        if (!string.Equals(Path.GetFileName(common), ".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ExitCodes.BadInput, $"repository '{common}' has no main worktree (bare or --separate-git-dir)", "run from a normal clone");
        }

        return new RepoPaths(Path.GetFullPath(top.StdOut.Trim()), Path.GetDirectoryName(common)!, common);
    }
}
```

- [ ] **Step 4: Implement `StatePaths` and `StateLayout`** (`src/Swarm.RunState/StatePaths.cs`)

```csharp
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Resolves and guards run-state and worktree paths.</summary>
public static class StatePaths
{
    /// <summary>Longest accepted state-dir or worktree path: Windows MAX_PATH (260) breaks git and dotnet children inside it.</summary>
    public const int MaxPathLength = 200;

    /// <summary>Resolves the state directory: relative paths are under the MAIN worktree, so every worktree shares one state.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="configured">Configured state dir (relative or absolute).</param>
    /// <returns>The guarded absolute path.</returns>
    /// <exception cref="ToolException">Empty or too long (exit code 2).</exception>
    public static string Resolve(RepoPaths repo, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new ToolException(ExitCodes.Usage, "stateDir must not be empty");
        }

        var full = Path.IsPathFullyQualified(configured) ? configured : Path.Combine(repo.MainWorktreeRoot, configured);
        return Guard(Path.GetFullPath(full), "state dir");
    }

    /// <summary>Resolves the worktree root (default: a sibling of the main worktree named <c>&lt;repo&gt;-wt</c>).</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="configured">Configured absolute root, or null.</param>
    /// <returns>The guarded absolute path.</returns>
    /// <exception cref="ToolException">Too long (exit code 2).</exception>
    public static string ResolveWorktreeRoot(RepoPaths repo, string? configured)
    {
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(repo.MainWorktreeRoot) ?? repo.MainWorktreeRoot, Path.GetFileName(repo.MainWorktreeRoot) + "-wt")
            : configured;
        return Guard(Path.GetFullPath(root), "worktree root");
    }

    /// <summary>Rejects over-long paths before anything is created.</summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <param name="what">Name used in the message (e.g. "state dir").</param>
    /// <returns><paramref name="fullPath"/>.</returns>
    /// <exception cref="ToolException">Longer than <see cref="MaxPathLength"/> (exit code 2).</exception>
    public static string Guard(string fullPath, string what) =>
        fullPath.Length <= MaxPathLength
            ? fullPath
            : throw new ToolException(ExitCodes.Usage, $"{what} path is {fullPath.Length} chars (limit {MaxPathLength})", "use a shorter path; Windows MAX_PATH breaks git and dotnet children");
}

/// <summary>Layout of a run-state directory.</summary>
/// <param name="Root">The state directory.</param>
public sealed record StateLayout(string Root)
{
    /// <summary>Gets the slot lock directory (one <c>slot-k.lock</c> per held slot).</summary>
    public string SlotsDir => Path.Combine(Root, "slots");

    /// <summary>Gets the directory of per-run folders.</summary>
    public string RunsDir => Path.Combine(Root, "runs");

    /// <summary>Gets the testgate event log.</summary>
    public string GateEventsFile => Path.Combine(Root, "testgate.events.jsonl");

    /// <summary>Gets the lock directory that serialises batch runs of one epic.</summary>
    /// <param name="epic">Epic id.</param>
    /// <returns>The directory.</returns>
    public string BatchLockDir(string epic) => Path.Combine(Root, "locks", "batch-" + epic);

    /// <summary>Gets one run's folder.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>The directory.</returns>
    public string RunDir(string runId) => Path.Combine(RunsDir, runId);
}
```

- [ ] **Step 5: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~StatePathsTests`
Expected: 9 PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "Resolve main worktree and run-state paths with length guard" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 5: Configuration (`.swarm/batch.json`)

**Files:**
- Create: `src/Swarm.RunState/SwarmConfig.cs`, `src/Swarm.RunState/ConfigLoader.cs`
- Create: `tests/Swarm.Tools.Tests/Support/TestConfig.cs`
- Test: `tests/Swarm.Tools.Tests/RunState/ConfigLoaderTests.cs`

**Interfaces:**
- Consumes: `RepoPaths` (Task 4), `ToolException`, `SafeName` (Task 1), `TempRepo`, `FakeSuite` (Task 2).
- Produces (namespace `Swarm.RunState`):
  - `sealed record BatchSizeConfig { int Start = 4; int Min = 2; int Max = 8; }` (init properties).
  - `sealed record SwarmConfig` with init properties and defaults: `int SchemaVersion = 1`, `int Slots = 2`, `BatchSizeConfig Batch`, `int ExpirySec = 60`, `int HeartbeatSec = 5`, `int PollMs = 200`, `int MaxWaitSec = 3600` (0 = forever), `string BaseBranch = "main"`, `string Epic = "E1"`, `string EpicBranchTemplate = "epic/{epic}"`, `IReadOnlyList<string> TestCommand = ["dotnet", "test"]`, `string StateDir = ".docs/runs"`, `string? WorktreeRoot = null`, `int MaxRebaseAttempts = 1`, `bool Prebatch = true`, `int KeepRuns = 20`; computed `[JsonIgnore] string EpicBranch` (template with `{epic}` replaced).
  - `sealed record ConfigOverrides` (all nullable init: `Slots, Start, Min, Max, ExpirySec, HeartbeatSec, PollMs, MaxWaitSec, Epic, StateDir, TestCommand`) with `static ConfigOverrides None` and `SwarmConfig ApplyTo(SwarmConfig config)`.
  - `static class ConfigLoader { const string DefaultRelativePath = ".swarm/batch.json"; static SwarmConfig Load(RepoPaths repo, string? explicitPath, ConfigOverrides overrides); static SwarmConfig Parse(string json, string sourceName); static IReadOnlyList<string> Check(SwarmConfig config); static SwarmConfig Validated(SwarmConfig config, string sourceName); }` — all failures `ToolException(Usage, "<source>: <first error>", "see docs/batch-tools.md#configuration")`.
  - Test-only: `static class TestConfig { static SwarmConfig For(TempRepo repo, params string[] fakeSuiteArgs); static string Write(TempRepo repo, SwarmConfig config); }` (`For`: slots 1, expiry 6, heartbeat 1, poll 50, maxWait 60, epic E1, FakeSuite test command, state/worktree roots inside the sandbox; `Write` -> `<Sandbox>\batch.json`).

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/RunState/ConfigLoaderTests.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class ConfigLoaderTests
{
    static void WriteDefaultConfig(TempRepo repo, string json)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".swarm"));
        File.WriteAllText(Path.Combine(repo.Root, ".swarm", "batch.json"), json);
    }

    [Fact]
    public void NoFile_UsesDefaults()
    {
        using var repo = TempRepo.Create();
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), null, ConfigOverrides.None);
        Assert.Equal(2, c.Slots);
        Assert.Equal((4, 2, 8), (c.Batch.Start, c.Batch.Min, c.Batch.Max));
        Assert.Equal(3600, c.MaxWaitSec);
        Assert.Equal(new[] { "dotnet", "test" }, c.TestCommand);
        Assert.Equal("epic/E1", c.EpicBranch);
    }

    [Fact]
    public void MainWorktreeFile_IsUsedFromLinkedWorktree()
    {
        using var repo = TempRepo.Create();
        WriteDefaultConfig(repo, """{ "slots": 3, "epic": "E7" }""");
        var side = Path.Combine(repo.Sandbox, "side");
        repo.Git("worktree", "add", "-q", "-b", "side", side);
        var c = ConfigLoader.Load(RepoLocator.Locate(side), null, ConfigOverrides.None);
        Assert.Equal(3, c.Slots);
        Assert.Equal("epic/E7", c.EpicBranch);
    }

    [Fact]
    public void FlagsWinOverFile()
    {
        using var repo = TempRepo.Create();
        WriteDefaultConfig(repo, """{ "slots": 3, "batch": { "start": 4, "min": 2, "max": 8 } }""");
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), null, new ConfigOverrides { Slots = 1, Max = 6 });
        Assert.Equal(1, c.Slots);
        Assert.Equal(6, c.Batch.Max);
        Assert.Equal(4, c.Batch.Start);
    }

    [Fact]
    public void CrlfAndComments_Accepted()
    {
        var c = ConfigLoader.Parse("{\r\n  // two slots\r\n  \"slots\": 2,\r\n  \"testCommand\": [\"npm\", \"test\"],\r\n}\r\n", "x.json");
        Assert.Equal(new[] { "npm", "test" }, c.TestCommand);
    }

    [Fact]
    public void ExplicitMissingFile_IsUsage()
    {
        using var repo = TempRepo.Create();
        var e = Assert.Throws<ToolException>(() => ConfigLoader.Load(RepoLocator.Locate(repo.Root), Path.Combine(repo.Sandbox, "nope.json"), ConfigOverrides.None));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains("not found", e.Message);
    }

    [Theory]
    [InlineData("""{ "slots": 0 }""", "slots must be >= 1")]
    [InlineData("""{ "batch": { "start": 2, "min": 4, "max": 8 } }""", "batch sizes must satisfy 1 <= min <= start <= max <= 64")]
    [InlineData("""{ "expirySec": 10, "heartbeatSec": 5 }""", "expirySec must be >= 3 x heartbeatSec")]
    [InlineData("""{ "heartbeatSec": 0 }""", "heartbeatSec must be >= 1")]
    [InlineData("""{ "pollMs": 5 }""", "pollMs must be between 10 and 60000")]
    [InlineData("""{ "maxWaitSec": -1 }""", "maxWaitSec must be >= 0")]
    [InlineData("""{ "testCommand": [] }""", "testCommand must be a non-empty array")]
    [InlineData("""{ "testCommand": ["dotnet", " "] }""", "testCommand must be a non-empty array")]
    [InlineData("""{ "epic": "E 1" }""", "epic must be a safe name")]
    [InlineData("""{ "epicBranchTemplate": "epic/x" }""", "epicBranchTemplate must contain {epic}")]
    [InlineData("""{ "baseBranch": "-x" }""", "baseBranch must be a branch name")]
    [InlineData("""{ "maxRebaseAttempts": 9 }""", "maxRebaseAttempts must be between 0 and 3")]
    [InlineData("""{ "keepRuns": 0 }""", "keepRuns must be >= 1")]
    [InlineData("""{ "worktreeRoot": "relative/wt" }""", "worktreeRoot must be an absolute path")]
    [InlineData("""{ "schemaVersion": 2 }""", "schemaVersion must be 1")]
    [InlineData("""{ "slot": 2 }""", "'slot'")]
    [InlineData("""{ "testCommand": null }""", "invalid config")]
    [InlineData("""not json""", "invalid config")]
    public void InvalidConfig_IsOneLineUsageError(string json, string expected)
    {
        var e = Assert.Throws<ToolException>(() => ConfigLoader.Validated(ConfigLoader.Parse(json, "batch.json"), "batch.json"));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("batch.json: ", e.Message);
        Assert.DoesNotContain('\n', e.ErrorLine);
    }

    [Fact]
    public void TestConfig_RoundTripsThroughFile()
    {
        using var repo = TempRepo.Create();
        var path = TestConfig.Write(repo, TestConfig.For(repo));
        var c = ConfigLoader.Load(RepoLocator.Locate(repo.Root), path, ConfigOverrides.None);
        Assert.Equal(1, c.Slots);
        Assert.Equal(repo.StateDir, c.StateDir);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~ConfigLoaderTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `SwarmConfig`** (`src/Swarm.RunState/SwarmConfig.cs`)

```csharp
using System.Text.Json.Serialization;

namespace Swarm.RunState;

/// <summary>Adaptive batch-size bounds.</summary>
public sealed record BatchSizeConfig
{
    /// <summary>Gets the first batch size.</summary>
    public int Start { get; init; } = 4;

    /// <summary>Gets the smallest batch size (after red batches).</summary>
    public int Min { get; init; } = 2;

    /// <summary>Gets the largest batch size (after green batches).</summary>
    public int Max { get; init; } = 8;
}

/// <summary>Shared configuration of testgate and batch (<c>.swarm/batch.json</c>, schema version 1).</summary>
public sealed record SwarmConfig
{
    /// <summary>Gets the schema version (must be 1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the number of concurrent test slots on this machine.</summary>
    public int Slots { get; init; } = 2;

    /// <summary>Gets the batch-size bounds.</summary>
    public BatchSizeConfig Batch { get; init; } = new();

    /// <summary>Gets the age in seconds after which an unrefreshed slot lock is stale.</summary>
    public int ExpirySec { get; init; } = 60;

    /// <summary>Gets the heartbeat interval in seconds.</summary>
    public int HeartbeatSec { get; init; } = 5;

    /// <summary>Gets the slot polling interval in milliseconds.</summary>
    public int PollMs { get; init; } = 200;

    /// <summary>Gets the maximum seconds to wait for a slot (0 = forever) before exit 5.</summary>
    public int MaxWaitSec { get; init; } = 3600;

    /// <summary>Gets the base branch (consumed by epic creation in Plan C).</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>Gets the epic id.</summary>
    public string Epic { get; init; } = "E1";

    /// <summary>Gets the epic branch template; <c>{epic}</c> is replaced by <see cref="Epic"/>.</summary>
    public string EpicBranchTemplate { get; init; } = "epic/{epic}";

    /// <summary>Gets the full-suite command (program and arguments).</summary>
    public IReadOnlyList<string> TestCommand { get; init; } = ["dotnet", "test"];

    /// <summary>Gets the run-state directory; relative paths are under the main worktree.</summary>
    public string StateDir { get; init; } = ".docs/runs";

    /// <summary>Gets the absolute root for tool worktrees, or null for <c>&lt;main parent&gt;/&lt;repo&gt;-wt</c>.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>Gets how many times a conflicting task is automatically rebased (copy ref) and requeued.</summary>
    public int MaxRebaseAttempts { get; init; } = 1;

    /// <summary>Gets a value indicating whether tasks touching the same files are kept in separate batches.</summary>
    public bool Prebatch { get; init; } = true;

    /// <summary>Gets how many finished runs are kept under the state dir.</summary>
    public int KeepRuns { get; init; } = 20;

    /// <summary>Gets the epic branch name.</summary>
    [JsonIgnore]
    public string EpicBranch => EpicBranchTemplate.Replace("{epic}", Epic, StringComparison.Ordinal);
}

/// <summary>Command-line values that override the config file (null = not given).</summary>
public sealed record ConfigOverrides
{
    /// <summary>Gets no overrides.</summary>
    public static ConfigOverrides None { get; } = new();

    /// <summary>Gets the slot count override.</summary>
    public int? Slots { get; init; }

    /// <summary>Gets the start batch size override.</summary>
    public int? Start { get; init; }

    /// <summary>Gets the minimum batch size override.</summary>
    public int? Min { get; init; }

    /// <summary>Gets the maximum batch size override.</summary>
    public int? Max { get; init; }

    /// <summary>Gets the expiry override.</summary>
    public int? ExpirySec { get; init; }

    /// <summary>Gets the heartbeat override.</summary>
    public int? HeartbeatSec { get; init; }

    /// <summary>Gets the poll interval override.</summary>
    public int? PollMs { get; init; }

    /// <summary>Gets the maximum wait override.</summary>
    public int? MaxWaitSec { get; init; }

    /// <summary>Gets the epic id override.</summary>
    public string? Epic { get; init; }

    /// <summary>Gets the state dir override (already absolute when it comes from a flag).</summary>
    public string? StateDir { get; init; }

    /// <summary>Gets the test command override.</summary>
    public IReadOnlyList<string>? TestCommand { get; init; }

    /// <summary>Applies the overrides (flags win).</summary>
    /// <param name="config">Config from file or defaults.</param>
    /// <returns>The merged config (not yet validated).</returns>
    public SwarmConfig ApplyTo(SwarmConfig config) => config with
    {
        Slots = Slots ?? config.Slots,
        Batch = config.Batch with { Start = Start ?? config.Batch.Start, Min = Min ?? config.Batch.Min, Max = Max ?? config.Batch.Max },
        ExpirySec = ExpirySec ?? config.ExpirySec,
        HeartbeatSec = HeartbeatSec ?? config.HeartbeatSec,
        PollMs = PollMs ?? config.PollMs,
        MaxWaitSec = MaxWaitSec ?? config.MaxWaitSec,
        Epic = Epic ?? config.Epic,
        StateDir = StateDir ?? config.StateDir,
        TestCommand = TestCommand ?? config.TestCommand,
    };
}
```

- [ ] **Step 4: Implement `ConfigLoader`** (`src/Swarm.RunState/ConfigLoader.cs`)

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Loads, merges and validates <see cref="SwarmConfig"/>.</summary>
public static class ConfigLoader
{
    /// <summary>Default config location, relative to the main worktree.</summary>
    public const string DefaultRelativePath = ".swarm/batch.json";

    const string Hint = "see docs/batch-tools.md#configuration";

    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>Loads config: explicit file, else the main worktree's <c>.swarm/batch.json</c>, else defaults; then flags; then validation.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="explicitPath">Absolute path from <c>--config</c>, or null.</param>
    /// <param name="overrides">Flag values.</param>
    /// <returns>The validated config.</returns>
    /// <exception cref="ToolException">Missing explicit file, invalid JSON or invalid values (exit code 2).</exception>
    public static SwarmConfig Load(RepoPaths repo, string? explicitPath, ConfigOverrides overrides)
    {
        string source;
        SwarmConfig config;
        if (explicitPath is not null)
        {
            source = Path.GetFullPath(explicitPath);
            if (!File.Exists(source))
            {
                throw new ToolException(ExitCodes.Usage, $"config file '{source}' not found");
            }

            config = Parse(File.ReadAllText(source), source);
        }
        else
        {
            var path = Path.Combine(repo.MainWorktreeRoot, ".swarm", "batch.json");
            source = File.Exists(path) ? path : "defaults";
            config = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new SwarmConfig();
        }

        return Validated(overrides.ApplyTo(config), source);
    }

    /// <summary>Parses config JSON strictly (unknown keys and nulls rejected; comments and trailing commas allowed).</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns>The parsed config (not yet validated).</returns>
    /// <exception cref="ToolException">Invalid JSON (exit code 2).</exception>
    public static SwarmConfig Parse(string json, string sourceName)
    {
        try
        {
            return JsonSerializer.Deserialize<SwarmConfig>(json, Strict)
                ?? throw new ToolException(ExitCodes.Usage, $"{sourceName}: invalid config: must be a JSON object", Hint);
        }
        catch (JsonException e)
        {
            throw new ToolException(ExitCodes.Usage, $"{sourceName}: invalid config: {e.Message}", Hint);
        }
    }

    /// <summary>Lists every validation error.</summary>
    /// <param name="c">The config.</param>
    /// <returns>Errors, empty when valid.</returns>
    public static IReadOnlyList<string> Check(SwarmConfig c)
    {
        var e = new List<string>();
        if (c.SchemaVersion != 1)
        {
            e.Add($"schemaVersion must be 1 (got {c.SchemaVersion})");
        }

        if (c.Slots < 1)
        {
            e.Add($"slots must be >= 1 (got {c.Slots})");
        }

        var b = c.Batch;
        if (!(b.Min >= 1 && b.Min <= b.Start && b.Start <= b.Max && b.Max <= 64))
        {
            e.Add($"batch sizes must satisfy 1 <= min <= start <= max <= 64 (got min {b.Min}, start {b.Start}, max {b.Max})");
        }

        if (c.HeartbeatSec < 1)
        {
            e.Add($"heartbeatSec must be >= 1 (got {c.HeartbeatSec})");
        }
        else if (c.ExpirySec < 3 * c.HeartbeatSec)
        {
            e.Add($"expirySec must be >= 3 x heartbeatSec (got expirySec {c.ExpirySec}, heartbeatSec {c.HeartbeatSec})");
        }

        if (c.PollMs is < 10 or > 60000)
        {
            e.Add($"pollMs must be between 10 and 60000 (got {c.PollMs})");
        }

        if (c.MaxWaitSec < 0)
        {
            e.Add($"maxWaitSec must be >= 0 (got {c.MaxWaitSec})");
        }

        if (c.TestCommand is null || c.TestCommand.Count == 0 || c.TestCommand.Any(string.IsNullOrWhiteSpace))
        {
            e.Add("testCommand must be a non-empty array of non-empty strings");
        }

        if (!SafeName.IsValid(c.Epic))
        {
            e.Add($"epic must be a safe name: {SafeName.Description} (got '{c.Epic}')");
        }

        if (c.EpicBranchTemplate is null || !c.EpicBranchTemplate.Contains("{epic}", StringComparison.Ordinal))
        {
            e.Add($"epicBranchTemplate must contain {{epic}} (got '{c.EpicBranchTemplate}')");
        }

        if (string.IsNullOrWhiteSpace(c.BaseBranch) || c.BaseBranch.StartsWith('-') || c.BaseBranch.Any(char.IsWhiteSpace))
        {
            e.Add($"baseBranch must be a branch name (got '{c.BaseBranch}')");
        }

        if (c.MaxRebaseAttempts is < 0 or > 3)
        {
            e.Add($"maxRebaseAttempts must be between 0 and 3 (got {c.MaxRebaseAttempts})");
        }

        if (c.KeepRuns < 1)
        {
            e.Add($"keepRuns must be >= 1 (got {c.KeepRuns})");
        }

        if (string.IsNullOrWhiteSpace(c.StateDir))
        {
            e.Add("stateDir must not be empty");
        }

        if (c.WorktreeRoot is { } w && !Path.IsPathFullyQualified(w))
        {
            e.Add($"worktreeRoot must be an absolute path (got '{w}')");
        }

        return e;
    }

    /// <summary>Validates and returns the config.</summary>
    /// <param name="config">The config.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns><paramref name="config"/>.</returns>
    /// <exception cref="ToolException">The first validation error (exit code 2).</exception>
    public static SwarmConfig Validated(SwarmConfig config, string sourceName)
    {
        var errors = Check(config);
        return errors.Count == 0 ? config : throw new ToolException(ExitCodes.Usage, $"{sourceName}: {errors[0]}", Hint);
    }
}
```

- [ ] **Step 5: Implement the test helper** (`tests/Swarm.Tools.Tests/Support/TestConfig.cs`)

```csharp
using System.Text.Json;
using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

public static class TestConfig
{
    public static SwarmConfig For(TempRepo repo, params string[] fakeSuiteArgs) => new()
    {
        Slots = 1,
        ExpirySec = 6,
        HeartbeatSec = 1,
        PollMs = 50,
        MaxWaitSec = 60,
        Epic = "E1",
        TestCommand = FakeSuite.Command(fakeSuiteArgs),
        StateDir = repo.StateDir,
        WorktreeRoot = repo.WorktreeRoot,
    };

    public static string Write(TempRepo repo, SwarmConfig config)
    {
        var path = Path.Combine(repo.Sandbox, "batch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~ConfigLoaderTests`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Add strict .swarm/batch.json config with flag overrides" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 6: JSON output types, JSONL ledgers, events, run directories, progress

**Files:**
- Create: `src/Swarm.RunState/SwarmJson.cs`, `OutputTypes.cs`, `JsonlFile.cs`, `EventLog.cs`, `ReturnLedger.cs`, `RunDirectories.cs`, `Progress.cs`
- Test: `tests/Swarm.Tools.Tests/RunState/JsonlFileTests.cs`, `tests/Swarm.Tools.Tests/RunState/RunFilesTests.cs`

**Interfaces:**
- Consumes: `SharedFile`, `FileTree`, `SafeName`, `ToolException` (Task 1); `StateLayout` (Task 4); `TempDir` (Task 2).
- Produces (namespace `Swarm.RunState`; all records serialise camelCase with `schemaVersion` first):
  - `static class SwarmJson { const int SchemaVersion = 1; static JsonSerializerOptions Compact; static JsonSerializerOptions Indented; static string Line<T>(T value); static void WriteFile<T>(string path, T value); static T Read<T>(string path); }` (`WriteFile`: indented, LF, trailing newline, temp file + atomic replace with retries).
  - `sealed record GateResult(int SchemaVersion, string Label, long WaitMs, long RunMs, int Slot, int ExitCode, bool Reclaimed, bool Killed, DateTime AcquiredUtc, DateTime ReleasedUtc)`.
  - String-constant classes: `ReturnKind { Conflict = "conflict", Red = "red", BadInput = "bad-input", Dependency = "dependency" }`; `ReturnStage { Preflight = "preflight", Merge = "merge", Land = "land", Suite = "suite" }`; `RebaseState { NotApplicable = "n/a", Pending = "pending", Clean = "clean", Conflict = "conflict", Skipped = "skipped" }`; `FinalState { Pending = "pending", Requeued = "requeued", RebasedAndLanded = "rebased-and-landed", NeedsWorker = "needs-worker", ReturnedRed = "returned-red", ReturnedBadInput = "returned-bad-input", BlockedByDependency = "blocked-by-dependency", NoOpAfterRebase = "no-op-after-rebase" }`.
  - `sealed record ReturnedEntry(int SchemaVersion, DateTime Utc, string RunId, string Task, string Branch, string Kind, string Stage, int Batch, IReadOnlyList<string> ConflictingWith, IReadOnlyList<string> Files, string Reason, string GitOutput, string Rebase, string? RebasedBranch, string RebaseOutput, string Final)` (`Branch` = the worker's branch; `RebasedBranch` = the copy ref when one exists).
  - `sealed record SuiteRecord(GateResult Gate, string LogFile, IReadOnlyList<string> Tasks)`, `sealed record LandedRecord(string Id, int Batch, string Commit, string Branch)`, `sealed record BatchLogEntry(int Batch, bool Bisect, IReadOnlyList<string> Tasks, string Result)`.
  - `sealed record BatchSummary(int SchemaVersion, string RunId, string Epic, string EpicBranch, string Mode, string Lander, int ExitCode, string? Note, int Tasks, int TasksLanded, int Returned, int RebasedAndLanded, int NeedsWorker, int RejectedRed, int BadInput, IReadOnlyList<string> Unprocessed, int FullSuiteRuns, int BisectRuns, int InferredRedSkipped, int Batches, IReadOnlyList<int> SizeTrace, double WallSeconds, long WaitMs, long RunMs, IReadOnlyList<SuiteRecord> Suites, IReadOnlyList<LandedRecord> Landed, IReadOnlyList<BatchLogEntry> BatchLog, IReadOnlyDictionary<string, IReadOnlyList<string>> DerivedTouches, string ReturnedFile, string EventsFile)`.
  - `static class JsonlFile { static void Append<T>(string path, T record); static IReadOnlyList<T> ReadAll<T>(string path); }` (one write per line under an exclusive-write open with retries; `ReadAll` skips a trailing incomplete line).
  - `static class EventTypes` constants: `run-start, batch-start, merge, conflict, suite, bisect, land, rebase, requeue, run-end, gate`; `sealed record RunEvent(int SchemaVersion, DateTime Utc, string RunId, string Type, object? Data)`; `sealed class EventLog(string filePath, string runId) { string FilePath; void Write(string type, object? data = null); }`.
  - `sealed class ReturnLedger(string filePath, string runId)` with `string FilePath`, `IReadOnlyCollection<ReturnedEntry> Latest`, `ReturnedEntry? Get(string taskId)`, `IReadOnlyList<ReturnedEntry> Pending()` (latest entries with `Rebase == pending`), `ReturnedEntry Record(ReturnedEntry entry)` (stamps schemaVersion/utc/runId, appends, becomes latest), `ReturnedEntry Update(string taskId, Func<ReturnedEntry, ReturnedEntry> change)`, `static ReturnedEntry New(string taskId, string branch, string kind, string stage, int batch, string reason)`, `static IReadOnlyDictionary<string, ReturnedEntry> ReadLatest(string filePath)` (last line per task wins).
  - `static class RunDirectories { const string SummaryFileName = "summary.json"; static string NewRunId(string epic, DateTime utc); static string Create(StateLayout layout, string runId); static IReadOnlyList<string> Prune(StateLayout layout, int keep); }` (`NewRunId` = `yyyyMMdd-HHmmss-fff-<epic>`; `Create` makes `<run>/logs`, rejects unsafe or existing ids with exit 2; `Prune` deletes only finished runs beyond the newest `keep`).
  - `enum Verbosity { Quiet, Normal, Detail }`; `sealed class Progress(TextWriter stderr, Verbosity verbosity) { void Info(string line); void Detail(string line); void Warn(string line); }` (`Warn` always prints `warning: <one line>`).

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/RunState/JsonlFileTests.cs`:

```csharp
using System.Text.Json;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class JsonlFileTests
{
    public sealed record Line(int Writer, int N);

    [Fact]
    public async Task ParallelAppends_AllLinesValid()
    {
        // Separate FileStreams per writer exercise the same OS sharing rules as separate processes.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "events.jsonl");
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                JsonlFile.Append(path, new Line(w, n));
            }
        }));
        await Task.WhenAll(writers);
        var lines = JsonlFile.ReadAll<Line>(path);
        Assert.Equal(400, lines.Count);
        Assert.Equal(400, lines.Distinct().Count());
    }

    [Fact]
    public void ReadAll_SkipsIncompleteLastLine()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "x.jsonl");
        JsonlFile.Append(path, new Line(1, 1));
        File.AppendAllText(path, "{\"writer\":2,");
        Assert.Single(JsonlFile.ReadAll<Line>(path));
    }

    [Fact]
    public void ReturnLedger_LastLinePerTaskWins()
    {
        using var dir = new TempDir();
        var ledger = new ReturnLedger(Path.Combine(dir.Dir, "returned.jsonl"), "run1");
        ledger.Record(ReturnLedger.New("T2", "task/T2", ReturnKind.Conflict, ReturnStage.Merge, 1, "merge conflict") with { Rebase = RebaseState.Pending });
        ledger.Update("T2", e => e with { Rebase = RebaseState.Clean, RebasedBranch = "rebased/E1/T2", Final = FinalState.Requeued });
        Assert.Equal(2, File.ReadAllLines(ledger.FilePath).Length);
        var latest = ReturnLedger.ReadLatest(ledger.FilePath);
        Assert.Equal(FinalState.Requeued, latest["T2"].Final);
        Assert.Equal("run1", latest["T2"].RunId);
        Assert.Equal(1, latest["T2"].SchemaVersion);
        Assert.Empty(ledger.Pending());
    }

    [Fact]
    public void EventLog_WritesRunEvents()
    {
        using var dir = new TempDir();
        var log = new EventLog(Path.Combine(dir.Dir, "events.jsonl"), "run1");
        log.Write(EventTypes.RunStart, new { tasks = 3 });
        var line = File.ReadAllLines(log.FilePath).Single();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("run-start", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("data").GetProperty("tasks").GetInt32());
    }
}
```

`tests/Swarm.Tools.Tests/RunState/RunFilesTests.cs`:

```csharp
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class RunFilesTests
{
    static readonly DateTime T0 = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GateResult_IsCamelCaseWithSchemaVersionFirst() =>
        Assert.StartsWith("{\"schemaVersion\":1,\"label\":\"x\",\"waitMs\":", SwarmJson.Line(new GateResult(1, "x", 2, 3, 0, 0, false, false, T0, T0)));

    [Fact]
    public void WriteFile_IsLfWithTrailingNewline()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "sub", "summary.json");
        SwarmJson.WriteFile(path, new BatchLogEntry(1, false, ["T1"], "green"));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
        Assert.Equal("green", SwarmJson.Read<BatchLogEntry>(path).Result);
    }

    [Fact]
    public void NewRunId_IsSafe() => Assert.True(SafeName.IsValid(RunDirectories.NewRunId("E1", T0)));

    [Fact]
    public void Create_RejectsExistingOrUnsafeIds()
    {
        using var dir = new TempDir();
        var layout = new StateLayout(dir.Dir);
        RunDirectories.Create(layout, "r1");
        Assert.True(Directory.Exists(Path.Combine(layout.RunDir("r1"), "logs")));
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => RunDirectories.Create(layout, "r1")).ExitCode);
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => RunDirectories.Create(layout, "../x")).ExitCode);
    }

    [Fact]
    public void Prune_KeepsNewestFinishedRunsAndAllUnfinished()
    {
        using var dir = new TempDir();
        var layout = new StateLayout(dir.Dir);
        for (var i = 1; i <= 4; i++)
        {
            var run = RunDirectories.Create(layout, $"r{i}");
            var summary = Path.Combine(run, RunDirectories.SummaryFileName);
            File.WriteAllText(summary, "{}");
            File.SetLastWriteTimeUtc(summary, T0.AddMinutes(i));
        }

        RunDirectories.Create(layout, "crashed");
        var deleted = RunDirectories.Prune(layout, keep: 2);
        Assert.Equal(new[] { "r1", "r2" }, deleted.Order());
        Assert.True(Directory.Exists(layout.RunDir("crashed")));
        Assert.True(Directory.Exists(layout.RunDir("r4")));
    }

    [Fact]
    public void Progress_RespectsVerbosity()
    {
        var err = new StringWriter();
        var quiet = new Progress(err, Verbosity.Quiet);
        quiet.Info("info");
        quiet.Detail("detail");
        quiet.Warn("careful\nnow");
        Assert.Equal("warning: careful now", err.ToString().TrimEnd());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~JsonlFileTests|FullyQualifiedName~RunFilesTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `SwarmJson`** (`src/Swarm.RunState/SwarmJson.cs`)

```csharp
using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>JSON conventions for every swarm tool output (camelCase, schema version 1, LF).</summary>
public static class SwarmJson
{
    /// <summary>Schema version written into every output object.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Gets single-line options (stdout lines, JSONL).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Gets indented options (files).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Serialises a value to one line.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>JSON without line breaks.</returns>
    public static string Line<T>(T value) => JsonSerializer.Serialize(value, Compact);

    /// <summary>Writes an indented JSON file with LF endings, replacing any existing file atomically.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="path">Target file.</param>
    /// <param name="value">The value.</param>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.tmp-{Environment.ProcessId}";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Indented) + "\n");
        SharedFile.Retry(() => File.Move(temp, path, overwrite: true));
    }

    /// <summary>Reads a JSON file.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The file holds JSON null.</exception>
    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Compact) ?? throw new InvalidDataException($"{path} holds no value");

    static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web) { WriteIndented = indented, NewLine = "\n" };
}
```

- [ ] **Step 4: Implement output types** (`src/Swarm.RunState/OutputTypes.cs`)

```csharp
namespace Swarm.RunState;

/// <summary>One gated run: the single stdout line of <c>testgate run</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Label">Caller-chosen label.</param>
/// <param name="WaitMs">Time spent waiting for a slot.</param>
/// <param name="RunMs">Time the command ran.</param>
/// <param name="Slot">Slot index held.</param>
/// <param name="ExitCode">The command's exit code (-1 when killed).</param>
/// <param name="Reclaimed">True when the slot was taken over from a stale lock.</param>
/// <param name="Killed">True when the command's process tree was killed.</param>
/// <param name="AcquiredUtc">When the slot was acquired.</param>
/// <param name="ReleasedUtc">When the slot was released.</param>
public sealed record GateResult(int SchemaVersion, string Label, long WaitMs, long RunMs, int Slot, int ExitCode, bool Reclaimed, bool Killed, DateTime AcquiredUtc, DateTime ReleasedUtc);

/// <summary>Why a task came back (<see cref="ReturnedEntry.Kind"/>).</summary>
public static class ReturnKind
{
    /// <summary>Merge or land conflict.</summary>
    public const string Conflict = "conflict";

    /// <summary>The suite was red with this task (bisect blame).</summary>
    public const string Red = "red";

    /// <summary>The task's branch does not exist.</summary>
    public const string BadInput = "bad-input";

    /// <summary>A task it depends on was returned.</summary>
    public const string Dependency = "dependency";
}

/// <summary>Where a task came back (<see cref="ReturnedEntry.Stage"/>).</summary>
public static class ReturnStage
{
    /// <summary>Before any merge.</summary>
    public const string Preflight = "preflight";

    /// <summary>Merging into the integration worktree.</summary>
    public const string Merge = "merge";

    /// <summary>Landing on the epic branch.</summary>
    public const string Land = "land";

    /// <summary>Running the full suite.</summary>
    public const string Suite = "suite";
}

/// <summary>Automatic rebase status (<see cref="ReturnedEntry.Rebase"/>).</summary>
public static class RebaseState
{
    /// <summary>No rebase applies (red, bad input).</summary>
    public const string NotApplicable = "n/a";

    /// <summary>A rebase will be attempted after the current batch.</summary>
    public const string Pending = "pending";

    /// <summary>The copy ref rebased cleanly.</summary>
    public const string Clean = "clean";

    /// <summary>The copy ref conflicted again.</summary>
    public const string Conflict = "conflict";

    /// <summary>No rebase: attempts used up, or the task is part of a stack.</summary>
    public const string Skipped = "skipped";
}

/// <summary>Final status of a returned task (<see cref="ReturnedEntry.Final"/>).</summary>
public static class FinalState
{
    /// <summary>Not decided yet.</summary>
    public const string Pending = "pending";

    /// <summary>Rebased copy requeued for a later batch.</summary>
    public const string Requeued = "requeued";

    /// <summary>Rebased copy landed (counts as landed).</summary>
    public const string RebasedAndLanded = "rebased-and-landed";

    /// <summary>Back to the worker: conflict that rebase cannot fix.</summary>
    public const string NeedsWorker = "needs-worker";

    /// <summary>Back to the worker: suite red.</summary>
    public const string ReturnedRed = "returned-red";

    /// <summary>Back to the caller: branch missing.</summary>
    public const string ReturnedBadInput = "returned-bad-input";

    /// <summary>Back to the caller: depends on a returned task.</summary>
    public const string BlockedByDependency = "blocked-by-dependency";

    /// <summary>The rebased copy has nothing left to land (its change is already on the epic).</summary>
    public const string NoOpAfterRebase = "no-op-after-rebase";
}

/// <summary>One line of <c>returned.jsonl</c>: a full snapshot of a task's return record (the last line per task wins).</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Utc">When this snapshot was written.</param>
/// <param name="RunId">The batch run.</param>
/// <param name="Task">Task id.</param>
/// <param name="Branch">The worker's branch (never modified by the tool).</param>
/// <param name="Kind">A <see cref="ReturnKind"/> value.</param>
/// <param name="Stage">A <see cref="ReturnStage"/> value.</param>
/// <param name="Batch">Batch number (0 = preflight).</param>
/// <param name="ConflictingWith">Task ids whose changes overlap the conflicting files (empty = the epic tip).</param>
/// <param name="Files">Conflicting files.</param>
/// <param name="Reason">One-line reason for the worker.</param>
/// <param name="GitOutput">Git's merge or land output.</param>
/// <param name="Rebase">A <see cref="RebaseState"/> value.</param>
/// <param name="RebasedBranch">The copy ref (<c>rebased/&lt;epic&gt;/&lt;task&gt;</c>) when one exists.</param>
/// <param name="RebaseOutput">Git's rebase output.</param>
/// <param name="Final">A <see cref="FinalState"/> value.</param>
public sealed record ReturnedEntry(
    int SchemaVersion, DateTime Utc, string RunId, string Task, string Branch, string Kind, string Stage, int Batch,
    IReadOnlyList<string> ConflictingWith, IReadOnlyList<string> Files, string Reason, string GitOutput,
    string Rebase, string? RebasedBranch, string RebaseOutput, string Final);

/// <summary>One full-suite run inside a batch.</summary>
/// <param name="Gate">The gate result.</param>
/// <param name="LogFile">The suite's combined output.</param>
/// <param name="Tasks">Task ids in the tested state.</param>
public sealed record SuiteRecord(GateResult Gate, string LogFile, IReadOnlyList<string> Tasks);

/// <summary>A task that landed on the epic branch.</summary>
/// <param name="Id">Task id.</param>
/// <param name="Batch">Batch number.</param>
/// <param name="Commit">The epic commit that contains it.</param>
/// <param name="Branch">The branch that was landed (worker branch or rebased copy).</param>
public sealed record LandedRecord(string Id, int Batch, string Commit, string Branch);

/// <summary>One tested set (a batch or a bisect half).</summary>
/// <param name="Batch">Batch number.</param>
/// <param name="Bisect">True for a bisect half.</param>
/// <param name="Tasks">Task ids merged into the tested state.</param>
/// <param name="Result">green, red, red (inferred), or a no-suite note.</param>
public sealed record BatchLogEntry(int Batch, bool Bisect, IReadOnlyList<string> Tasks, string Result);

/// <summary><c>summary.json</c> and the single stdout line of <c>batch run</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="RunId">Run id.</param>
/// <param name="Epic">Epic id.</param>
/// <param name="EpicBranch">Epic branch.</param>
/// <param name="Mode">batched or serial.</param>
/// <param name="Lander">The lander's name.</param>
/// <param name="ExitCode">The process exit code for this run.</param>
/// <param name="Note">Why the run stopped early, or a note (e.g. "empty batch").</param>
/// <param name="Tasks">Tasks in the file.</param>
/// <param name="TasksLanded">Tasks landed (including after rebase).</param>
/// <param name="Returned">Tasks returned and not landed.</param>
/// <param name="RebasedAndLanded">Tasks landed through a rebased copy.</param>
/// <param name="NeedsWorker">Tasks whose final state is needs-worker.</param>
/// <param name="RejectedRed">Tasks returned red.</param>
/// <param name="BadInput">Tasks returned for a missing branch or a returned dependency.</param>
/// <param name="Unprocessed">Tasks neither landed nor returned (the run stopped early).</param>
/// <param name="FullSuiteRuns">Suite runs, including bisect runs.</param>
/// <param name="BisectRuns">Suite runs on bisect halves.</param>
/// <param name="InferredRedSkipped">Suite runs skipped because the result was inferred red.</param>
/// <param name="Batches">Top-level batches.</param>
/// <param name="SizeTrace">Batch size used for each batch.</param>
/// <param name="WallSeconds">Wall time.</param>
/// <param name="WaitMs">Total slot wait.</param>
/// <param name="RunMs">Total suite run time.</param>
/// <param name="Suites">Every suite run.</param>
/// <param name="Landed">Every landed task.</param>
/// <param name="BatchLog">Every tested set.</param>
/// <param name="DerivedTouches">Files each task changes, derived from git at the start of the run.</param>
/// <param name="ReturnedFile">Path of <c>returned.jsonl</c>.</param>
/// <param name="EventsFile">Path of <c>events.jsonl</c>.</param>
public sealed record BatchSummary(
    int SchemaVersion, string RunId, string Epic, string EpicBranch, string Mode, string Lander, int ExitCode, string? Note,
    int Tasks, int TasksLanded, int Returned, int RebasedAndLanded, int NeedsWorker, int RejectedRed, int BadInput, IReadOnlyList<string> Unprocessed,
    int FullSuiteRuns, int BisectRuns, int InferredRedSkipped, int Batches, IReadOnlyList<int> SizeTrace,
    double WallSeconds, long WaitMs, long RunMs,
    IReadOnlyList<SuiteRecord> Suites, IReadOnlyList<LandedRecord> Landed, IReadOnlyList<BatchLogEntry> BatchLog,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DerivedTouches, string ReturnedFile, string EventsFile);
```

- [ ] **Step 5: Implement JSONL, events and the ledger**

`src/Swarm.RunState/JsonlFile.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Append-only JSON-lines files shared by several processes.</summary>
public static class JsonlFile
{
    /// <summary>Appends one record as one line.</summary>
    /// <typeparam name="T">Record type.</typeparam>
    /// <param name="path">The file (created with its directory).</param>
    /// <param name="record">The record.</param>
    public static void Append<T>(string path, T record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = Encoding.UTF8.GetBytes(SwarmJson.Line(record) + "\n");

        // FileShare.Read admits one writer at a time: a concurrent writer gets a sharing violation and retries.
        SharedFile.Retry(
            () =>
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            },
            attempts: 400,
            delayMs: 10);
    }

    /// <summary>Reads every complete line.</summary>
    /// <typeparam name="T">Record type.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>Records in file order; empty when the file is missing.</returns>
    public static IReadOnlyList<T> ReadAll<T>(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        // The element after the last '\n' is empty, or a line still being written: skip it either way.
        var lines = text.Split('\n');
        return lines[..^1].Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<T>(l, SwarmJson.Compact)!).ToList();
    }
}
```

`src/Swarm.RunState/EventLog.cs`:

```csharp
namespace Swarm.RunState;

/// <summary>Event type names written to <c>events.jsonl</c>.</summary>
public static class EventTypes
{
    /// <summary>A batch run started.</summary>
    public const string RunStart = "run-start";

    /// <summary>A top-level batch started.</summary>
    public const string BatchStart = "batch-start";

    /// <summary>A set was merged into the integration worktree.</summary>
    public const string Merge = "merge";

    /// <summary>A task conflicted.</summary>
    public const string Conflict = "conflict";

    /// <summary>A suite ran.</summary>
    public const string Suite = "suite";

    /// <summary>A red set was split.</summary>
    public const string Bisect = "bisect";

    /// <summary>A task landed.</summary>
    public const string Land = "land";

    /// <summary>A returned task was rebased.</summary>
    public const string Rebase = "rebase";

    /// <summary>Tasks went back to the queue.</summary>
    public const string Requeue = "requeue";

    /// <summary>A batch run ended.</summary>
    public const string RunEnd = "run-end";

    /// <summary>A testgate run finished.</summary>
    public const string Gate = "gate";
}

/// <summary>One line of an event log.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Utc">When it happened.</param>
/// <param name="RunId">Run id (or <c>testgate</c>).</param>
/// <param name="Type">An <see cref="EventTypes"/> value.</param>
/// <param name="Data">Event-specific payload.</param>
public sealed record RunEvent(int SchemaVersion, DateTime Utc, string RunId, string Type, object? Data);

/// <summary>Appends <see cref="RunEvent"/> lines to a JSONL file.</summary>
/// <param name="filePath">The events file.</param>
/// <param name="runId">Run id stamped on every event.</param>
public sealed class EventLog(string filePath, string runId)
{
    /// <summary>Gets the events file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Appends one event.</summary>
    /// <param name="type">An <see cref="EventTypes"/> value.</param>
    /// <param name="data">Payload (serialised with its runtime type).</param>
    public void Write(string type, object? data = null) =>
        JsonlFile.Append(FilePath, new RunEvent(SwarmJson.SchemaVersion, DateTime.UtcNow, runId, type, data));
}
```

`src/Swarm.RunState/ReturnLedger.cs`:

```csharp
namespace Swarm.RunState;

/// <summary>Writes <c>returned.jsonl</c> and tracks the latest record per task.</summary>
/// <param name="filePath">The ledger file.</param>
/// <param name="runId">Run id stamped on every record.</param>
public sealed class ReturnLedger(string filePath, string runId)
{
    readonly Dictionary<string, ReturnedEntry> latest = new(StringComparer.Ordinal);

    /// <summary>Gets the ledger file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Gets the latest record of every returned task.</summary>
    public IReadOnlyCollection<ReturnedEntry> Latest => latest.Values;

    /// <summary>Creates a record with empty lists, no rebase and final <c>pending</c> (not yet written).</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="branch">The worker's branch.</param>
    /// <param name="kind">A <see cref="ReturnKind"/> value.</param>
    /// <param name="stage">A <see cref="ReturnStage"/> value.</param>
    /// <param name="batch">Batch number.</param>
    /// <param name="reason">One-line reason.</param>
    /// <returns>The record.</returns>
    public static ReturnedEntry New(string taskId, string branch, string kind, string stage, int batch, string reason) =>
        new(SwarmJson.SchemaVersion, default, "", taskId, branch, kind, stage, batch, [], [], reason, "", RebaseState.NotApplicable, null, "", FinalState.Pending);

    /// <summary>Reads a ledger file and keeps the last record per task.</summary>
    /// <param name="filePath">The ledger file.</param>
    /// <returns>Task id to latest record.</returns>
    public static IReadOnlyDictionary<string, ReturnedEntry> ReadLatest(string filePath) =>
        JsonlFile.ReadAll<ReturnedEntry>(filePath).GroupBy(e => e.Task, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    /// <summary>Gets a task's latest record.</summary>
    /// <param name="taskId">Task id.</param>
    /// <returns>The record, or null.</returns>
    public ReturnedEntry? Get(string taskId) => latest.GetValueOrDefault(taskId);

    /// <summary>Gets the latest records waiting for a rebase.</summary>
    /// <returns>Records with <c>rebase: pending</c>.</returns>
    public IReadOnlyList<ReturnedEntry> Pending() => latest.Values.Where(e => e.Rebase == RebaseState.Pending).ToList();

    /// <summary>Stamps and appends a record; it becomes the task's latest.</summary>
    /// <param name="entry">The record.</param>
    /// <returns>The stamped record.</returns>
    public ReturnedEntry Record(ReturnedEntry entry)
    {
        var stamped = entry with { SchemaVersion = SwarmJson.SchemaVersion, Utc = DateTime.UtcNow, RunId = runId };
        JsonlFile.Append(FilePath, stamped);
        latest[stamped.Task] = stamped;
        return stamped;
    }

    /// <summary>Changes a task's latest record and appends the new snapshot.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="change">The change.</param>
    /// <returns>The stamped record.</returns>
    /// <exception cref="InvalidOperationException">The task has no record.</exception>
    public ReturnedEntry Update(string taskId, Func<ReturnedEntry, ReturnedEntry> change) =>
        Record(change(Get(taskId) ?? throw new InvalidOperationException($"task '{taskId}' has no returned record")));
}
```

- [ ] **Step 6: Implement run directories and progress**

`src/Swarm.RunState/RunDirectories.cs`:

```csharp
using System.Globalization;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Creates and prunes per-run folders under <c>&lt;state&gt;/runs</c>.</summary>
public static class RunDirectories
{
    /// <summary>File whose presence marks a finished run.</summary>
    public const string SummaryFileName = "summary.json";

    /// <summary>Builds a timestamped run id.</summary>
    /// <param name="epic">Epic id.</param>
    /// <param name="utc">Start time.</param>
    /// <returns><c>yyyyMMdd-HHmmss-fff-&lt;epic&gt;</c>.</returns>
    public static string NewRunId(string epic, DateTime utc) => utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + epic;

    /// <summary>Creates a run folder with a <c>logs</c> subfolder.</summary>
    /// <param name="layout">State layout.</param>
    /// <param name="runId">Run id.</param>
    /// <returns>The run folder.</returns>
    /// <exception cref="ToolException">Unsafe or existing id (exit code 2).</exception>
    public static string Create(StateLayout layout, string runId)
    {
        if (!SafeName.IsValid(runId))
        {
            throw new ToolException(ExitCodes.Usage, $"run id '{runId}' is not valid ({SafeName.Description})");
        }

        var dir = layout.RunDir(runId);
        if (Directory.Exists(dir))
        {
            throw new ToolException(ExitCodes.Usage, $"run id '{runId}' already exists in {layout.RunsDir}", "choose another --run-id");
        }

        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        return dir;
    }

    /// <summary>Deletes finished runs beyond the newest <paramref name="keep"/>; unfinished runs are never deleted.</summary>
    /// <param name="layout">State layout.</param>
    /// <param name="keep">Finished runs to keep.</param>
    /// <returns>Names of deleted runs.</returns>
    public static IReadOnlyList<string> Prune(StateLayout layout, int keep)
    {
        if (!Directory.Exists(layout.RunsDir))
        {
            return [];
        }

        var old = new DirectoryInfo(layout.RunsDir).GetDirectories()
            .Select(d => (Dir: d, Summary: Path.Combine(d.FullName, SummaryFileName)))
            .Where(x => File.Exists(x.Summary))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Summary))
            .Skip(keep)
            .Select(x => x.Dir)
            .ToList();
        foreach (var dir in old)
        {
            FileTree.DeleteTree(dir.FullName);
        }

        return old.Select(d => d.Name).ToList();
    }
}
```

`src/Swarm.RunState/Progress.cs`:

```csharp
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>How much progress text goes to stderr.</summary>
public enum Verbosity
{
    /// <summary>Errors and warnings only.</summary>
    Quiet,

    /// <summary>One line per step.</summary>
    Normal,

    /// <summary>Also command output.</summary>
    Detail,
}

/// <summary>Human progress on stderr (stdout is reserved for the JSON result).</summary>
/// <param name="stderr">Destination.</param>
/// <param name="verbosity">Level.</param>
public sealed class Progress(TextWriter stderr, Verbosity verbosity)
{
    /// <summary>Writes a step line at normal verbosity.</summary>
    /// <param name="line">The line.</param>
    public void Info(string line)
    {
        if (verbosity >= Verbosity.Normal)
        {
            stderr.WriteLine(line);
        }
    }

    /// <summary>Writes a detail line (command output) at detail verbosity.</summary>
    /// <param name="line">The line.</param>
    public void Detail(string line)
    {
        if (verbosity >= Verbosity.Detail)
        {
            stderr.WriteLine(line);
        }
    }

    /// <summary>Writes a one-line warning at any verbosity.</summary>
    /// <param name="line">The warning.</param>
    public void Warn(string line) => stderr.WriteLine("warning: " + TextLines.OneLine(line));
}
```

- [ ] **Step 7: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~JsonlFileTests|FullyQualifiedName~RunFilesTests"`
Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "Add JSON output types, JSONL ledgers, events and run folders" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 7: Slot semaphore (lock files, heartbeat, expiry, reclaim)

**Files:**
- Create: `src/Swarm.RunState/SlotSemaphore.cs`
- Test: `tests/Swarm.Tools.Tests/RunState/SlotSemaphoreTests.cs`

**Interfaces:**
- Consumes: `StatePaths.Guard` (Task 4), `SwarmConfig` (Task 5), `SwarmJson`, `GateResult` (Task 6), `SharedFile`, `TextLines`, `ToolException` (Task 1).
- Produces (namespace `Swarm.RunState`):
  - `sealed record SlotOptions(int Slots, TimeSpan Expiry, TimeSpan Heartbeat, TimeSpan Poll, TimeSpan? MaxWait)` with `static SlotOptions From(SwarmConfig config)` (`MaxWaitSec == 0` -> `MaxWait = null`, wait forever).
  - `sealed record LockInfo(int Pid, string Host, string Command, DateTime AcquiredUtc, DateTime ProcessStartUtc)` (content of `slot-k.lock`).
  - `sealed record SlotHolder(int Slot, LockInfo? Info, double HeartbeatAgeSec, bool Stale, bool HolderAlive)`.
  - `sealed record GateStatus(int SchemaVersion, string LockDir, int Slots, IReadOnlyList<SlotHolder> Holders)` (stdout of `testgate status`).
  - `sealed record ReclaimReport(int SchemaVersion, IReadOnlyList<int> Reclaimed, IReadOnlyList<int> SkippedLive)` (stdout of `testgate reclaim --force`).
  - `sealed class SlotLease : IDisposable { int Slot; bool Reclaimed; TimeSpan Waited; DateTime AcquiredUtc; string LockPath; void Dispose(); }` (heartbeat every `Heartbeat` via the held handle; `Dispose` is idempotent: stop heartbeat, close, delete with retries).
  - `sealed class SlotSemaphore(string lockDir, SlotOptions options)` with `string LockDir` (guarded <= 200 chars), `SlotOptions Options`, `SlotLease Acquire(string command, CancellationToken cancellationToken = default)` (polls; past `MaxWait` -> `ToolException(GateTimeout, "no test slot free after <n> s (<slots> slots in <dir>)", "raise --max-wait, or run 'testgate status' to see holders")`), `SlotLease? TryAcquire(string command)` (one pass, no wait), `IReadOnlyList<SlotHolder> Status()`, `ReclaimReport Reclaim()` (deletes stale locks and locks whose holder pid is dead on this host; never a live local holder), `static bool IsHolderAlive(LockInfo? info)`.

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/RunState/SlotSemaphoreTests.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.RunState;

public class SlotSemaphoreTests
{
    static SlotOptions Options(int slots, double expirySec = 3, double heartbeatSec = 1, double? maxWaitSec = null) =>
        new(slots, TimeSpan.FromSeconds(expirySec), TimeSpan.FromSeconds(heartbeatSec), TimeSpan.FromMilliseconds(20), maxWaitSec is { } m ? TimeSpan.FromSeconds(m) : null);

    [Fact]
    public void Acquire_UsesFreeSlotsThenNone()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        var a = sem.Acquire("a");
        using var b = sem.Acquire("b");
        Assert.Equal((0, 1), (a.Slot, b.Slot));
        Assert.Null(sem.TryAcquire("c"));
        a.Dispose();
        Assert.False(File.Exists(a.LockPath));
        using var c = sem.TryAcquire("c");
        Assert.Equal(0, c!.Slot);
    }

    [Fact]
    public async Task ConcurrentHolders_NeverExceedSlots()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        var inside = 0;
        var peak = 0;
        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            using var lease = sem.Acquire($"worker {i}");
            var now = Interlocked.Increment(ref inside);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            Thread.Sleep(150);
            Interlocked.Decrement(ref inside);
        }));
        await Task.WhenAll(workers);
        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public void StaleLock_IsReclaimed()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "slot-0.lock");
        File.WriteAllText(path, "left by a crashed holder");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-120));
        using var lease = new SlotSemaphore(dir.Dir, Options(1)).Acquire("me");
        Assert.True(lease.Reclaimed);
        Assert.Equal(0, lease.Slot);
    }

    [Fact]
    public void Heartbeat_KeepsLockFreshPastExpiry()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(1, expirySec: 3, heartbeatSec: 1));
        using var lease = sem.Acquire("holder");
        Thread.Sleep(3500);
        Assert.True((DateTime.UtcNow - File.GetLastWriteTimeUtc(lease.LockPath)).TotalSeconds < 1.6);
        Assert.Null(sem.TryAcquire("intruder"));
    }

    [Fact]
    public void MaxWait_ThrowsGateTimeout()
    {
        using var dir = new TempDir();
        using var held = new SlotSemaphore(dir.Dir, Options(1)).Acquire("holder");
        var e = Assert.Throws<ToolException>(() => new SlotSemaphore(dir.Dir, Options(1, maxWaitSec: 0.3)).Acquire("waiter"));
        Assert.Equal(ExitCodes.GateTimeout, e.ExitCode);
        Assert.Contains("no test slot free after", e.Message);
    }

    [Fact]
    public void LiveLock_CannotBeDeletedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        using var lease = new SlotSemaphore(dir.Dir, Options(1)).Acquire("holder");
        Assert.NotNull(Record.Exception(() => File.Delete(lease.LockPath)));
        Assert.True(File.Exists(lease.LockPath));
    }

    [Fact]
    public void Status_ReportsHolderInfo()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(2));
        using var lease = sem.Acquire("dotnet test");
        var holder = Assert.Single(sem.Status());
        Assert.Equal(Environment.ProcessId, holder.Info!.Pid);
        Assert.Equal("dotnet test", holder.Info.Command);
        Assert.False(holder.Stale);
        Assert.True(holder.HolderAlive);
    }

    [Fact]
    public void Reclaim_RemovesStaleAndDeadButNotLive()
    {
        using var dir = new TempDir();
        var sem = new SlotSemaphore(dir.Dir, Options(3, expirySec: 30));
        using var live = sem.Acquire("live");
        var dead = new LockInfo(int.MaxValue - 7, Environment.MachineName, "dead", DateTime.UtcNow, DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(dir.Dir, "slot-1.lock"), SwarmJson.Line(dead));
        var stale = Path.Combine(dir.Dir, "slot-2.lock");
        File.WriteAllText(stale, "garbage");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5));
        var report = sem.Reclaim();
        Assert.Equal(new[] { 1, 2 }, report.Reclaimed);
        Assert.Equal(new[] { 0 }, report.SkippedLive);
        Assert.True(File.Exists(live.LockPath));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~SlotSemaphoreTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** (`src/Swarm.RunState/SlotSemaphore.cs`)

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Slot semaphore settings.</summary>
/// <param name="Slots">Number of slots.</param>
/// <param name="Expiry">Age after which an unrefreshed lock is stale.</param>
/// <param name="Heartbeat">How often a holder refreshes its lock.</param>
/// <param name="Poll">How often a waiter retries.</param>
/// <param name="MaxWait">Longest wait before a gate timeout, or null for no limit.</param>
public sealed record SlotOptions(int Slots, TimeSpan Expiry, TimeSpan Heartbeat, TimeSpan Poll, TimeSpan? MaxWait)
{
    /// <summary>Builds options from config.</summary>
    /// <param name="config">The config.</param>
    /// <returns>The options (<c>maxWaitSec: 0</c> means no limit).</returns>
    public static SlotOptions From(SwarmConfig config) => new(
        config.Slots,
        TimeSpan.FromSeconds(config.ExpirySec),
        TimeSpan.FromSeconds(config.HeartbeatSec),
        TimeSpan.FromMilliseconds(config.PollMs),
        config.MaxWaitSec == 0 ? null : TimeSpan.FromSeconds(config.MaxWaitSec));
}

/// <summary>Content of a slot lock file.</summary>
/// <param name="Pid">Holder process id.</param>
/// <param name="Host">Holder machine name.</param>
/// <param name="Command">What the holder runs.</param>
/// <param name="AcquiredUtc">When the slot was taken.</param>
/// <param name="ProcessStartUtc">Holder process start time (guards against pid reuse).</param>
public sealed record LockInfo(int Pid, string Host, string Command, DateTime AcquiredUtc, DateTime ProcessStartUtc);

/// <summary>A lock file as seen by <c>status</c>.</summary>
/// <param name="Slot">Slot index.</param>
/// <param name="Info">Parsed content, or null when unreadable (e.g. mid-write).</param>
/// <param name="HeartbeatAgeSec">Seconds since the last heartbeat.</param>
/// <param name="Stale">True when older than the expiry.</param>
/// <param name="HolderAlive">True when the holder is a running process (holders on other hosts count as alive).</param>
public sealed record SlotHolder(int Slot, LockInfo? Info, double HeartbeatAgeSec, bool Stale, bool HolderAlive);

/// <summary>stdout of <c>testgate status</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="LockDir">The slot lock directory.</param>
/// <param name="Slots">Configured slot count.</param>
/// <param name="Holders">Current lock files.</param>
public sealed record GateStatus(int SchemaVersion, string LockDir, int Slots, IReadOnlyList<SlotHolder> Holders);

/// <summary>stdout of <c>testgate reclaim --force</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Reclaimed">Slots whose lock was deleted.</param>
/// <param name="SkippedLive">Slots left alone because the holder is alive.</param>
public sealed record ReclaimReport(int SchemaVersion, IReadOnlyList<int> Reclaimed, IReadOnlyList<int> SkippedLive);

/// <summary>A held slot. Dispose to release it.</summary>
public sealed class SlotLease : IDisposable
{
    readonly FileStream stream;
    readonly Timer heartbeat;
    int disposed;

    internal SlotLease(int slot, string lockPath, FileStream stream, bool reclaimed, TimeSpan waited, DateTime acquiredUtc, TimeSpan heartbeatEvery)
    {
        Slot = slot;
        LockPath = lockPath;
        this.stream = stream;
        Reclaimed = reclaimed;
        Waited = waited;
        AcquiredUtc = acquiredUtc;
        heartbeat = new Timer(_ => Beat(), null, heartbeatEvery, heartbeatEvery);
    }

    /// <summary>Gets the slot index.</summary>
    public int Slot { get; }

    /// <summary>Gets a value indicating whether the slot was taken over from a stale lock.</summary>
    public bool Reclaimed { get; }

    /// <summary>Gets the time spent waiting.</summary>
    public TimeSpan Waited { get; }

    /// <summary>Gets when the slot was acquired.</summary>
    public DateTime AcquiredUtc { get; }

    /// <summary>Gets the lock file.</summary>
    public string LockPath { get; }

    /// <summary>Stops the heartbeat and deletes the lock (idempotent).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        heartbeat.Dispose();
        stream.Dispose();
        try
        {
            SharedFile.Retry(() => File.Delete(LockPath), attempts: 20, delayMs: 25);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind (e.g. a scanner holds it): it goes stale and is reclaimed after the expiry.
        }
    }

    // Touch mtime through the held handle: no second open, so no sharing violation with our own handle.
    void Beat()
    {
        try
        {
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Released concurrently; nothing to refresh.
        }
    }
}

/// <summary>
/// Machine-wide counting semaphore made of lock files: slot k is <c>slot-k.lock</c>, created atomically with
/// <see cref="FileMode.CreateNew"/>, refreshed by a heartbeat, reclaimed when older than the expiry.
/// </summary>
public sealed class SlotSemaphore
{
    /// <summary>Initializes a new instance of the <see cref="SlotSemaphore"/> class.</summary>
    /// <param name="lockDir">Directory holding the lock files.</param>
    /// <param name="options">Settings.</param>
    /// <exception cref="ToolException">The lock directory path is too long (exit code 2).</exception>
    public SlotSemaphore(string lockDir, SlotOptions options)
    {
        LockDir = StatePaths.Guard(Path.GetFullPath(lockDir), "lock dir");
        Options = options;
    }

    /// <summary>Gets the lock directory.</summary>
    public string LockDir { get; }

    /// <summary>Gets the settings.</summary>
    public SlotOptions Options { get; }

    /// <summary>Decides whether a lock's holder is still running.</summary>
    /// <param name="info">Lock content.</param>
    /// <returns>False for unreadable locks and dead local pids (or reused pids); true for live local and all remote holders.</returns>
    public static bool IsHolderAlive(LockInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        if (!string.Equals(info.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var p = Process.GetProcessById(info.Pid);
            return Math.Abs((p.StartTime.ToUniversalTime() - info.ProcessStartUtc).TotalSeconds) < 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Waits for a free slot.</summary>
    /// <param name="command">What the holder will run (recorded in the lock).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The lease.</returns>
    /// <exception cref="ToolException">No slot within <see cref="SlotOptions.MaxWait"/> (exit code 5).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public SlotLease Acquire(string command, CancellationToken cancellationToken = default)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryOnce(command, waited) is { } lease)
            {
                return lease;
            }

            if (Options.MaxWait is { } max && waited.Elapsed >= max)
            {
                throw new ToolException(
                    ExitCodes.GateTimeout,
                    $"no test slot free after {max.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s ({Options.Slots} slots in {LockDir})",
                    "raise --max-wait, or run 'testgate status' to see holders");
            }

            cancellationToken.WaitHandle.WaitOne(Options.Poll);
        }
    }

    /// <summary>Tries every slot once.</summary>
    /// <param name="command">What the holder will run.</param>
    /// <returns>The lease, or null when every slot is held.</returns>
    public SlotLease? TryAcquire(string command) => TryOnce(command, Stopwatch.StartNew());

    /// <summary>Lists the current lock files.</summary>
    /// <returns>One entry per lock file, by slot.</returns>
    public IReadOnlyList<SlotHolder> Status()
    {
        if (!Directory.Exists(LockDir))
        {
            return [];
        }

        var holders = new List<SlotHolder>();
        foreach (var path in Directory.EnumerateFiles(LockDir, "slot-*.lock"))
        {
            var name = Path.GetFileNameWithoutExtension(path)["slot-".Length..];
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || !File.Exists(path))
            {
                continue;
            }

            var info = ReadInfo(path);
            var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            holders.Add(new SlotHolder(slot, info, Math.Round(age, 1), age > Options.Expiry.TotalSeconds, IsHolderAlive(info)));
        }

        return holders.OrderBy(h => h.Slot).ToList();
    }

    /// <summary>Deletes stale locks and locks of dead local holders; a live local holder's lock is never deleted.</summary>
    /// <returns>What was reclaimed and what was skipped.</returns>
    public ReclaimReport Reclaim()
    {
        var reclaimed = new List<int>();
        var live = new List<int>();
        foreach (var h in Status())
        {
            var deadLocal = h.Info is not null
                && string.Equals(h.Info.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                && !h.HolderAlive;
            if (!h.Stale && !deadLocal)
            {
                live.Add(h.Slot);
                continue;
            }

            try
            {
                File.Delete(PathOf(h.Slot));
                reclaimed.Add(h.Slot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still open by a running process (Windows refuses the delete): treat as live.
                live.Add(h.Slot);
            }
        }

        return new ReclaimReport(SwarmJson.SchemaVersion, reclaimed, live);
    }

    static FileStream? TryCreate(string path)
    {
        try
        {
            // CreateNew is the atomic acquire. No FileShare.Delete: nobody can delete a live holder's lock on Windows.
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Delete-pending file from a holder that is releasing.
            return null;
        }
    }

    static LockInfo? ReadInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<LockInfo>(reader.ReadToEnd(), SwarmJson.Compact);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    string PathOf(int slot) => Path.Combine(LockDir, $"slot-{slot.ToString(CultureInfo.InvariantCulture)}.lock");

    bool IsStale(string path) => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Options.Expiry;

    SlotLease? TryOnce(string command, Stopwatch waited)
    {
        Directory.CreateDirectory(LockDir);
        for (var k = 0; k < Options.Slots; k++)
        {
            var path = PathOf(k);
            var reclaimed = false;
            var stream = TryCreate(path);
            if (stream is null && IsStale(path))
            {
                // Stale heartbeat: the holder crashed. Re-check, delete, race on CreateNew again (the loser keeps waiting).
                try
                {
                    if (IsStale(path))
                    {
                        File.Delete(path);
                        stream = TryCreate(path);
                        reclaimed = stream is not null;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Another reclaimer won, or the holder is alive after all.
                }
            }

            if (stream is not null)
            {
                return Lease(k, path, stream, reclaimed, waited.Elapsed, command);
            }
        }

        return null;
    }

    SlotLease Lease(int slot, string path, FileStream stream, bool reclaimed, TimeSpan waited, string command)
    {
        using var me = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var info = new LockInfo(Environment.ProcessId, Environment.MachineName, TextLines.OneLine(command), now, me.StartTime.ToUniversalTime());
        stream.Write(Encoding.UTF8.GetBytes(SwarmJson.Line(info)));
        stream.Flush();
        return new SlotLease(slot, path, stream, reclaimed, waited, now, Options.Heartbeat);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~SlotSemaphoreTests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add lock-file slot semaphore with heartbeat, expiry and reclaim" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 8: Gate runner, shared CLI host, and the `testgate` tool

**Files:**
- Create: `src/Swarm.Gate/GateRunner.cs`, `src/Swarm.RunState/Cli/CliHost.cs`, `src/Swarm.RunState/Cli/CommonOptions.cs`
- Modify: `src/Swarm.TestGate.Cli/Program.cs` (replace the placeholder)
- Test: `tests/Swarm.Tools.Tests/Gate/GateRunnerTests.cs`, `tests/Swarm.Tools.Tests/Cli/TestGateCliTests.cs`

**Interfaces:**
- Consumes: `SlotSemaphore`, `SlotOptions`, `GateStatus`, `ReclaimReport` (Task 7); `ProcessRunner`, `ProcessRunOptions` (Task 3); `RepoLocator` (Task 4); `ConfigLoader`, `ConfigOverrides` (Task 5); `GateResult`, `SwarmJson`, `EventLog`, `EventTypes`, `Progress`, `Verbosity` (Task 6); `ToolErrors` (Task 1).
- Produces:
  - `Swarm.Gate.GateRequest(IReadOnlyList<string> Command, string WorkingDirectory, string Label)`; `Swarm.Gate.GateRun(GateResult Result, string Log)`; `sealed class Swarm.Gate.GateRunner(SlotSemaphore slots, Action<string>? onOutputLine = null) { GateRun Run(GateRequest request, CancellationToken cancellationToken = default); }` — empty command -> `ToolException(Usage)`; missing working directory -> `ToolException(Usage)`; the slot is always released (also when the command cannot start or is cancelled).
  - `Swarm.RunState.Cli.CliHost { static int Invoke(RootCommand root, string[] args, TextWriter stdout, TextWriter stderr); }` (parse errors -> one line, exit 2; response files disabled; exceptions -> `ToolErrors.Handle`).
  - `sealed class Swarm.RunState.Cli.CtrlCScope : IDisposable { CancellationToken Token; }`.
  - `sealed record Swarm.RunState.Cli.ToolContext(RepoPaths Repo, SwarmConfig Config, StateLayout State, Verbosity Verbosity)`.
  - `sealed class Swarm.RunState.Cli.CommonOptions` with `Option<string?> ConfigOption` (`--config`), `Option<string?> StateOption` (`--state`), `Option<int?> SlotsOption` (`--slots`), `Option<int?> MaxWaitOption` (`--max-wait`, seconds), `Option<Verbosity> VerbosityOption` (`--verbosity`, default normal), `void AddTo(Command command)`, `ToolContext Resolve(ParseResult parse, string currentDirectory, ConfigOverrides extra)` (relative `--config`/`--state` are relative to `currentDirectory`).
  - CLI: `testgate run [common] [--cwd d] [--label l] [-- <cmd...>]` (no command -> config `testCommand`; stdout one `GateResult` line; exit 0 child ok, 1 child failed, 2/4/5 own failures); `testgate status [common]` (stdout `GateStatus`); `testgate reclaim --force [common]` (stdout `ReclaimReport`; without `--force` exit 2). `public static int Swarm.TestGate.Cli.Program.Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)`.

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/Gate/GateRunnerTests.cs`:

```csharp
using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Gate;

public class GateRunnerTests
{
    static SlotSemaphore Slots(string dir) =>
        new(Path.Combine(dir, "slots"), new SlotOptions(1, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), null));

    [Fact]
    public void Run_ReturnsChildExitCodeAndLog()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Dir, "x.fail"), "");
        var run = new GateRunner(Slots(dir.Dir)).Run(new GateRequest(FakeSuite.Command(), dir.Dir, "t"));
        Assert.Equal(1, run.Result.ExitCode);
        Assert.Equal("t", run.Result.Label);
        Assert.Contains("fake-suite: red (x.fail)", run.Log);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Dir, "slots")));
    }

    [Fact]
    public void CommandCannotStart_ReleasesSlot()
    {
        using var dir = new TempDir();
        var slots = Slots(dir.Dir);
        var e = Assert.Throws<ToolException>(() => new GateRunner(slots).Run(new GateRequest(["no-such-cmd-xyz"], dir.Dir, "t")));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        using var lease = slots.TryAcquire("next");
        Assert.NotNull(lease);
    }

    [Fact]
    public void EmptyCommand_IsUsage()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new GateRunner(Slots(dir.Dir)).Run(new GateRequest([], dir.Dir, "t"))).ExitCode);
    }
}
```

`tests/Swarm.Tools.Tests/Cli/TestGateCliTests.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using TestGateProgram = Swarm.TestGate.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class TestGateCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = TestGateProgram.Run(args, stdout, stderr, repo.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static string Config(TempRepo repo) => TestConfig.Write(repo, TestConfig.For(repo));

    static JsonElement SingleJsonLine(string stdout)
    {
        var line = Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return JsonDocument.Parse(line).RootElement;
    }

    [Fact]
    public void Run_GreenCommand_PrintsOneJsonLineAndExits0()
    {
        using var repo = TempRepo.Create();
        var (code, output, err) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command()]);
        Assert.Equal(0, code);
        var json = SingleJsonLine(output);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("fake-suite: green", err);
        Assert.True(File.Exists(new StateLayout(repo.StateDir).GateEventsFile));
    }

    [Fact]
    public void Run_RedCommand_Exits1WithChildCodeInJson()
    {
        using var repo = TempRepo.Create();
        File.WriteAllText(Path.Combine(repo.Root, "x.fail"), "");
        var (code, output, _) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command()]);
        Assert.Equal(1, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Run_NoCommand_UsesConfigTestCommand()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "run", "--config", Config(repo));
        Assert.Equal(0, code);
        Assert.Equal(0, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void AtSignArgument_IsPassedThrough()
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, ["run", "--config", Config(repo), "--", .. FakeSuite.Command("@notafile")]);
        Assert.Equal(0, code);
        Assert.Contains("@notafile", err);
    }

    [Fact]
    public void LongStatePath_Exits2OneLine()
    {
        using var repo = TempRepo.Create();
        var (code, output, err) = Run(repo, "run", "--config", Config(repo), "--state", Path.Combine(repo.Sandbox, new string('x', 220)), "--", "git", "--version");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Matches(new Regex(@"^error: state dir path is \d+ chars \(limit 200\)"), err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void MaxWaitTimeout_Exits5()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("holder");
        var (code, output, err) = Run(repo, ["run", "--config", TestConfig.Write(repo, config), "--max-wait", "1", "--", .. FakeSuite.Command()]);
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Empty(output);
        Assert.StartsWith("error: no test slot free after", err.TrimEnd());
    }

    [Fact]
    public void Status_ListsHolder()
    {
        using var repo = TempRepo.Create();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("holder");
        var (code, output, _) = Run(repo, "status", "--config", TestConfig.Write(repo, config));
        Assert.Equal(0, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("holders").GetArrayLength());
    }

    [Fact]
    public void Reclaim_WithoutForce_Exits2()
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, "reclaim", "--config", Config(repo));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal("error: reclaim deletes lock files (pass --force to confirm)", err.TrimEnd());
    }

    [Fact]
    public void Reclaim_WithForce_PrintsReport()
    {
        using var repo = TempRepo.Create();
        var (code, output, _) = Run(repo, "reclaim", "--force", "--config", Config(repo));
        Assert.Equal(0, code);
        Assert.Equal(0, SingleJsonLine(output).GetProperty("reclaimed").GetArrayLength());
    }

    [Theory]
    [InlineData("run", "--bogus")]
    [InlineData]
    [InlineData("nope")]
    public void ParseErrors_Exit2OneLine(params string[] args)
    {
        using var repo = TempRepo.Create();
        var (code, _, err) = Run(repo, args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.StartsWith("error: ", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void OutsideRepo_Exits3()
    {
        using var dir = new TempDir();
        var stderr = new StringWriter();
        Assert.Equal(ExitCodes.BadInput, TestGateProgram.Run(["status"], new StringWriter(), stderr, dir.Dir));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~GateRunnerTests|FullyQualifiedName~TestGateCliTests"`
Expected: FAIL to compile (`GateRunner`, `Program.Run` not defined).

- [ ] **Step 3: Implement `GateRunner`** (`src/Swarm.Gate/GateRunner.cs`)

```csharp
using System.Diagnostics;
using System.Text;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Gate;

/// <summary>A command to run under a test slot.</summary>
/// <param name="Command">Program and arguments.</param>
/// <param name="WorkingDirectory">Where to run it.</param>
/// <param name="Label">Label recorded in the result.</param>
public sealed record GateRequest(IReadOnlyList<string> Command, string WorkingDirectory, string Label);

/// <summary>A finished gated run.</summary>
/// <param name="Result">Timings, slot and exit code.</param>
/// <param name="Log">Combined stdout and stderr in arrival order.</param>
public sealed record GateRun(GateResult Result, string Log);

/// <summary>Acquires a slot, runs a command, always releases the slot.</summary>
/// <param name="slots">The slot semaphore.</param>
/// <param name="onOutputLine">Optional live output callback.</param>
public sealed class GateRunner(SlotSemaphore slots, Action<string>? onOutputLine = null)
{
    /// <summary>Runs one command under a slot.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels waiting or running (the process tree is killed).</param>
    /// <returns>The run.</returns>
    /// <exception cref="ToolException">Empty command or missing directory (2), cannot start (4), no slot in time (5).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public GateRun Run(GateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Command.Count == 0)
        {
            throw new ToolException(ExitCodes.Usage, "no command to run", "pass it after --, or set testCommand in .swarm/batch.json");
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new ToolException(ExitCodes.Usage, $"working directory '{request.WorkingDirectory}' does not exist");
        }

        var log = new StringBuilder();
        var lease = slots.Acquire(string.Join(' ', request.Command), cancellationToken);
        ProcessResult result;
        long runMs;
        try
        {
            var clock = Stopwatch.StartNew();
            result = ProcessRunner.Run(
                request.Command[0],
                request.Command.Skip(1).ToList(),
                request.WorkingDirectory,
                new ProcessRunOptions { OnLine = line => { log.Append(line).Append('\n'); onOutputLine?.Invoke(line); } },
                cancellationToken);
            runMs = clock.ElapsedMilliseconds;
        }
        finally
        {
            lease.Dispose();
        }

        var gate = new GateResult(SwarmJson.SchemaVersion, request.Label, (long)lease.Waited.TotalMilliseconds, runMs, lease.Slot, result.ExitCode, lease.Reclaimed, result.Killed, lease.AcquiredUtc, DateTime.UtcNow);
        return new GateRun(gate, log.ToString());
    }
}
```

- [ ] **Step 4: Implement the shared CLI host** (System.CommandLine 2.0.0 API: `Command.Parse(args, ParserConfiguration)`, `ParseResult.Invoke(InvocationConfiguration)`; if a member name differs in the pinned version, keep the behaviour that `TestGateCliTests.ParseErrors_Exit2OneLine` and `AtSignArgument_IsPassedThrough` pin)

`src/Swarm.RunState/Cli/CliHost.cs`:

```csharp
using System.CommandLine;
using Swarm.Git;

namespace Swarm.RunState.Cli;

/// <summary>Runs a System.CommandLine root with swarm conventions: one-line errors, exit 2 for usage.</summary>
public static class CliHost
{
    /// <summary>Parses and invokes.</summary>
    /// <param name="root">The root command.</param>
    /// <param name="args">Arguments.</param>
    /// <param name="stdout">Standard output (help, version, JSON).</param>
    /// <param name="stderr">Standard error.</param>
    /// <returns>The exit code.</returns>
    public static int Invoke(RootCommand root, string[] args, TextWriter stdout, TextWriter stderr)
    {
        // Response files off: an argument such as "@x" must reach the gated command untouched.
        var parse = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parse.Errors.Count > 0)
        {
            stderr.WriteLine(ToolException.Format(parse.Errors[0].Message, "see --help"));
            return ExitCodes.Usage;
        }

        return ToolErrors.Handle(
            () => parse.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false }),
            stderr);
    }
}

/// <summary>Cancels a token on Ctrl+C instead of killing the process, so slots are released and child trees killed.</summary>
public sealed class CtrlCScope : IDisposable
{
    readonly CancellationTokenSource source = new();
    readonly ConsoleCancelEventHandler handler;

    /// <summary>Initializes a new instance of the <see cref="CtrlCScope"/> class.</summary>
    public CtrlCScope()
    {
        handler = (_, e) =>
        {
            e.Cancel = true;
            source.Cancel();
        };
        Console.CancelKeyPress += handler;
    }

    /// <summary>Gets the token cancelled by Ctrl+C.</summary>
    public CancellationToken Token => source.Token;

    /// <summary>Unhooks the handler.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= handler;
        source.Dispose();
    }
}
```

`src/Swarm.RunState/Cli/CommonOptions.cs`:

```csharp
using System.CommandLine;
using Swarm.Git;

namespace Swarm.RunState.Cli;

/// <summary>Everything a command needs after option and config resolution.</summary>
/// <param name="Repo">The repository.</param>
/// <param name="Config">Validated config with flags applied.</param>
/// <param name="State">State layout.</param>
/// <param name="Verbosity">Progress level.</param>
public sealed record ToolContext(RepoPaths Repo, SwarmConfig Config, StateLayout State, Verbosity Verbosity);

/// <summary>Options shared by testgate and batch commands.</summary>
public sealed class CommonOptions
{
    /// <summary>Gets <c>--config</c>.</summary>
    public Option<string?> ConfigOption { get; } = new("--config") { Description = "Config file (default: <main worktree>/.swarm/batch.json)" };

    /// <summary>Gets <c>--state</c>.</summary>
    public Option<string?> StateOption { get; } = new("--state") { Description = "Run-state directory (default: config stateDir, .docs/runs in the main worktree)" };

    /// <summary>Gets <c>--slots</c>.</summary>
    public Option<int?> SlotsOption { get; } = new("--slots") { Description = "Concurrent test slots on this machine" };

    /// <summary>Gets <c>--max-wait</c>.</summary>
    public Option<int?> MaxWaitOption { get; } = new("--max-wait") { Description = "Seconds to wait for a slot before exit 5 (0 = forever)" };

    /// <summary>Gets <c>--verbosity</c>.</summary>
    public Option<Verbosity> VerbosityOption { get; } = new("--verbosity") { Description = "quiet, normal or detail", DefaultValueFactory = _ => Verbosity.Normal };

    /// <summary>Adds the shared options to a command.</summary>
    /// <param name="command">The command.</param>
    public void AddTo(Command command)
    {
        command.Options.Add(ConfigOption);
        command.Options.Add(StateOption);
        command.Options.Add(SlotsOption);
        command.Options.Add(MaxWaitOption);
        command.Options.Add(VerbosityOption);
    }

    /// <summary>Locates the repo, loads config (file, then flags) and resolves the state dir.</summary>
    /// <param name="parse">The parse result.</param>
    /// <param name="currentDirectory">Directory relative flag paths are based on.</param>
    /// <param name="extra">Command-specific overrides.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ToolException">Not in a repo (3), bad config or path (2).</exception>
    public ToolContext Resolve(ParseResult parse, string currentDirectory, ConfigOverrides extra)
    {
        var repo = RepoLocator.Locate(currentDirectory);
        string? Absolute(string? p) => p is null ? null : Path.GetFullPath(Path.Combine(currentDirectory, p));
        var overrides = extra with
        {
            Slots = parse.GetValue(SlotsOption) ?? extra.Slots,
            MaxWaitSec = parse.GetValue(MaxWaitOption) ?? extra.MaxWaitSec,
            StateDir = Absolute(parse.GetValue(StateOption)) ?? extra.StateDir,
        };
        var config = ConfigLoader.Load(repo, Absolute(parse.GetValue(ConfigOption)), overrides);
        return new ToolContext(repo, config, new StateLayout(StatePaths.Resolve(repo, config.StateDir)), parse.GetValue(VerbosityOption));
    }
}
```

- [ ] **Step 5: Implement the `testgate` CLI** (`src/Swarm.TestGate.Cli/Program.cs`)

```csharp
using System.CommandLine;
using System.Globalization;
using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.TestGate.Cli;

/// <summary>The <c>testgate</c> tool: a machine-wide counting semaphore for expensive test runs.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line per command.</param>
    /// <param name="stderr">Receives progress, child output and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();

        var cwd = new Option<string?>("--cwd") { Description = "Directory to run the command in (default: current directory)" };
        var label = new Option<string?>("--label") { Description = "Label recorded in the result (default: testgate)" };
        var command = new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore, Description = "Command after --; default: config testCommand" };
        var run = new Command("run", "Wait for a free test slot, run a command, release the slot; prints one JSON line") { cwd, label, command };
        common.AddTo(run);
        run.SetAction(p => RunGate(p, common, cwd, label, command, stdout, stderr, currentDirectory));

        var status = new Command("status", "List slot holders and heartbeat ages as one JSON line");
        common.AddTo(status);
        status.SetAction(p => Status(p, common, stdout, stderr, currentDirectory));

        var force = new Option<bool>("--force") { Description = "Confirm deleting stale or dead-holder lock files" };
        var reclaim = new Command("reclaim", "Delete stale or dead-holder slot locks (never a live local holder)") { force };
        common.AddTo(reclaim);
        reclaim.SetAction(p => Reclaim(p, common, force, stdout, currentDirectory));

        var root = new RootCommand("testgate - run expensive commands under a machine-wide slot limit") { run, status, reclaim };
        return CliHost.Invoke(root, args, stdout, stderr);
    }

    static int RunGate(ParseResult p, CommonOptions common, Option<string?> cwd, Option<string?> label, Argument<string[]> command, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        var cmd = p.GetValue(command) is { Length: > 0 } given ? given : ctx.Config.TestCommand.ToArray();
        var workDir = Path.GetFullPath(Path.Combine(currentDirectory, p.GetValue(cwd) ?? "."));
        var progress = new Progress(stderr, ctx.Verbosity);
        var slots = new SlotSemaphore(ctx.State.SlotsDir, SlotOptions.From(ctx.Config));
        using var ctrlC = new CtrlCScope();
        var gate = new GateRunner(slots, progress.Info).Run(new GateRequest(cmd, workDir, p.GetValue(label) ?? "testgate"), ctrlC.Token);
        new EventLog(ctx.State.GateEventsFile, "testgate").Write(EventTypes.Gate, gate.Result);
        stdout.WriteLine(SwarmJson.Line(gate.Result));
        return gate.Result.ExitCode == 0 ? ExitCodes.Ok : ExitCodes.Returned;
    }

    static int Status(ParseResult p, CommonOptions common, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        var slots = new SlotSemaphore(ctx.State.SlotsDir, SlotOptions.From(ctx.Config));
        var holders = slots.Status();
        var progress = new Progress(stderr, ctx.Verbosity);
        foreach (var h in holders)
        {
            var pid = h.Info?.Pid.ToString(CultureInfo.InvariantCulture) ?? "?";
            progress.Info($"slot {h.Slot}: pid {pid} on {h.Info?.Host ?? "?"}, heartbeat {h.HeartbeatAgeSec} s ago{(h.Stale ? " (STALE)" : "")}: {h.Info?.Command ?? "(unreadable)"}");
        }

        stdout.WriteLine(SwarmJson.Line(new GateStatus(SwarmJson.SchemaVersion, slots.LockDir, ctx.Config.Slots, holders)));
        return ExitCodes.Ok;
    }

    static int Reclaim(ParseResult p, CommonOptions common, Option<bool> force, TextWriter stdout, string currentDirectory)
    {
        if (!p.GetValue(force))
        {
            throw new ToolException(ExitCodes.Usage, "reclaim deletes lock files", "pass --force to confirm");
        }

        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        stdout.WriteLine(SwarmJson.Line(new SlotSemaphore(ctx.State.SlotsDir, SlotOptions.From(ctx.Config)).Reclaim()));
        return ExitCodes.Ok;
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~GateRunnerTests|FullyQualifiedName~TestGateCliTests"`
Expected: all PASS.

- [ ] **Step 7: Verify the package builds** (dnx smoke for both tools is Task 12)

Run: `cd <repo-root> && dotnet pack src/Swarm.TestGate.Cli -c Release -o .docs/feed -warnaserror`
Expected: `Successfully created package '...Swarm.TestGate.0.1.0.nupkg'`, 0 warnings. If NuGet emits a metadata warning (license/authors), record it for the human decision on package metadata; do not invent metadata.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "Add gate runner, shared CLI host and the testgate tool" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 9: Batch model: tasks file, stack units, pre-batching, sizing

**Files:**
- Create: `src/Swarm.Batching/TasksFile.cs`, `src/Swarm.Batching/TaskUnits.cs`, `src/Swarm.Batching/BatchPlanner.cs`
- Test: `tests/Swarm.Tools.Tests/Batching/TasksFileTests.cs`, `tests/Swarm.Tools.Tests/Batching/BatchPlannerTests.cs`

**Interfaces:**
- Consumes: `ToolException`, `SafeName` (Task 1).
- Produces (namespace `Swarm.Batching`):
  - `sealed record TaskSpec(string Id, string Branch, IReadOnlyList<string> DependsOn)` with `string BranchRef` (`refs/heads/<Branch>`).
  - `static class TasksFile { static IReadOnlyList<TaskSpec> Load(string path); static IReadOnlyList<TaskSpec> Parse(string json, string sourceName); }` — JSON array of `{ id, branch, dependsOn?, touches? (ignored), title? (ignored) }`; comments/trailing commas allowed; unknown keys, missing/unsafe ids, duplicate ids (case-insensitive), branches starting with `-` or containing whitespace/`..`/`~^:?*[\`, and `dependsOn` naming anything but an earlier task -> `ToolException(BadInput, "<source>: ...")`; missing file -> `ToolException(BadInput, "tasks file '<path>' not found")`.
  - `sealed record TaskUnit(IReadOnlyList<TaskSpec> Members)` with `string Id` (first member), `int Size`, `IEnumerable<string> Ids`.
  - `static class TaskUnits { static IReadOnlyList<TaskUnit> Build(IReadOnlyList<TaskSpec> tasks); }` — tasks connected by `dependsOn` (ignoring ids not in the list) form one unit, members in queue order, units ordered by first member.
  - `static class BatchPlanner { static int NextSize(int current, bool red, int min, int max); static IReadOnlyList<TaskUnit> PreBatch(IReadOnlyList<TaskUnit> queue, int size, Func<TaskUnit, TaskUnit, bool> overlaps, bool prebatch); static (IReadOnlyList<TaskUnit> Left, IReadOnlyList<TaskUnit> Right) Halve(IReadOnlyList<TaskUnit> units); }` — `NextSize`: green doubles up to max, red halves down to min; `PreBatch`: walks the queue, always takes the first unit, then takes units that fit the remaining size and (when `prebatch`) overlap none already taken; `Halve`: left = first `Count / 2` units.

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/Batching/TasksFileTests.cs`:

```csharp
using Swarm.Batching;
using Swarm.Git;

namespace Swarm.Tools.Tests.Batching;

public class TasksFileTests
{
    [Fact]
    public void ParsesTasksAndIgnoresTouches()
    {
        var tasks = TasksFile.Parse("""[ { "id": "T1", "branch": "task/T1", "touches": ["bogus.cs"] }, { "id": "T2", "branch": "task/T2", "dependsOn": ["T1"], "title": "x" } ]""", "tasks.json");
        Assert.Equal(new[] { "T1", "T2" }, tasks.Select(t => t.Id));
        Assert.Equal("refs/heads/task/T2", tasks[1].BranchRef);
        Assert.Equal(new[] { "T1" }, tasks[1].DependsOn);
        Assert.Empty(tasks[0].DependsOn);
    }

    [Fact]
    public void CrlfAndTrailingCommas_Accepted() =>
        Assert.Single(TasksFile.Parse("[\r\n  // one task\r\n  { \"id\": \"T1\", \"branch\": \"task/T1\", },\r\n]\r\n", "tasks.json"));

    [Fact]
    public void EmptyArray_IsValid() => Assert.Empty(TasksFile.Parse("[]", "tasks.json"));

    [Theory]
    [InlineData("""[ { "id": """, "invalid JSON")]
    [InlineData("""{ "id": "T1" }""", "invalid JSON")]
    [InlineData("""[ null ]""", "task #1: must be an object")]
    [InlineData("""[ { "branch": "task/T1" } ]""", "task #1: missing 'id'")]
    [InlineData("""[ { "id": "T1" } ]""", "task 'T1': missing 'branch'")]
    [InlineData("""[ { "id": "T 1", "branch": "b" } ]""", "task id 'T 1' is not a safe name")]
    [InlineData("""[ { "id": "T1", "branch": "a" }, { "id": "t1", "branch": "b" } ]""", "duplicate task id 't1'")]
    [InlineData("""[ { "id": "T1", "branch": "-x" } ]""", "task 'T1': branch '-x' is not a valid branch name")]
    [InlineData("""[ { "id": "T1", "branch": "a..b" } ]""", "task 'T1': branch 'a..b' is not a valid branch name")]
    [InlineData("""[ { "id": "T1", "branch": "a", "dependsOn": ["T2"] }, { "id": "T2", "branch": "b" } ]""", "task 'T1': dependsOn 'T2' must name an earlier task")]
    [InlineData("""[ { "id": "T1", "brnach": "a" } ]""", "'brnach'")]
    public void InvalidFile_IsBadInput(string json, string expected)
    {
        var e = Assert.Throws<ToolException>(() => TasksFile.Parse(json, "tasks.json"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("tasks.json: ", e.Message);
    }

    [Fact]
    public void MissingFile_IsBadInput() =>
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => TasksFile.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json"))).ExitCode);
}
```

`tests/Swarm.Tools.Tests/Batching/BatchPlannerTests.cs`:

```csharp
using Swarm.Batching;

namespace Swarm.Tools.Tests.Batching;

public class BatchPlannerTests
{
    static TaskSpec T(string id, params string[] deps) => new(id, "task/" + id, deps);

    static IReadOnlyList<TaskUnit> Units(params TaskSpec[] tasks) => TaskUnits.Build(tasks);

    [Fact]
    public void Units_GroupStacksAtFirstMemberPosition()
    {
        var units = Units(T("T1"), T("T2"), T("T3", "T1"), T("T4", "T3"));
        Assert.Equal(new[] { "T1+T3+T4", "T2" }, units.Select(u => string.Join('+', u.Ids)));
    }

    [Fact]
    public void Units_IgnoreDependenciesNotInList() =>
        Assert.Equal(2, Units(T("T3", "T1"), T("T4")).Count);

    [Theory]
    [InlineData(4, false, 8)]
    [InlineData(8, false, 8)]
    [InlineData(8, true, 4)]
    [InlineData(4, true, 2)]
    [InlineData(2, true, 2)]
    public void NextSize_DoublesOnGreenHalvesOnRed(int current, bool red, int expected) =>
        Assert.Equal(expected, BatchPlanner.NextSize(current, red, 2, 8));

    [Fact]
    public void PreBatch_SkipsOverlappingUnits()
    {
        var queue = Units(T("T1"), T("T2"), T("T3"));
        var pick = BatchPlanner.PreBatch(queue, 4, (a, b) => (a.Id, b.Id) is ("T1", "T2") or ("T2", "T1"), prebatch: true);
        Assert.Equal(new[] { "T1", "T3" }, pick.Select(u => u.Id));
    }

    [Fact]
    public void PreBatch_Disabled_TakesInOrder()
    {
        var queue = Units(T("T1"), T("T2"), T("T3"));
        Assert.Equal(3, BatchPlanner.PreBatch(queue, 4, (_, _) => true, prebatch: false).Count);
    }

    [Fact]
    public void PreBatch_CountsUnitSizesAndAlwaysTakesTheHead()
    {
        var queue = Units(T("T1"), T("T2", "T1"), T("T3", "T2"), T("T4"));
        var pick = BatchPlanner.PreBatch(queue, 2, (_, _) => false, prebatch: true);
        Assert.Equal(new[] { "T1" }, pick.Select(u => u.Id));
        Assert.Equal(3, pick[0].Size);
    }

    [Fact]
    public void PreBatch_FillsWithSmallerLaterUnits()
    {
        var queue = Units(T("T1"), T("T2"), T("T3", "T2"), T("T4"));
        var pick = BatchPlanner.PreBatch(queue, 2, (_, _) => false, prebatch: true);
        Assert.Equal(new[] { "T1", "T4" }, pick.Select(u => u.Id));
    }

    [Fact]
    public void Halve_LeftIsSmaller()
    {
        var (left, right) = BatchPlanner.Halve(Units(T("T1"), T("T2"), T("T3")));
        Assert.Equal(new[] { "T1" }, left.Select(u => u.Id));
        Assert.Equal(new[] { "T2", "T3" }, right.Select(u => u.Id));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~TasksFileTests|FullyQualifiedName~BatchPlannerTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `TasksFile`** (`src/Swarm.Batching/TasksFile.cs`)

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Swarm.Git;

namespace Swarm.Batching;

/// <summary>One task in queue order.</summary>
/// <param name="Id">Task id (a safe name).</param>
/// <param name="Branch">Branch to land (the worker's branch, or a rebased copy).</param>
/// <param name="DependsOn">Ids of earlier tasks this task is stacked on.</param>
public sealed record TaskSpec(string Id, string Branch, IReadOnlyList<string> DependsOn)
{
    /// <summary>Gets the full ref of <see cref="Branch"/>.</summary>
    public string BranchRef => "refs/heads/" + Branch;
}

/// <summary>Reads and validates a tasks file.</summary>
public static class TasksFile
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly string[] BadBranchParts = ["..", "~", "^", ":", "?", "*", "[", "\\", "@{"];

    /// <summary>Loads a tasks file.</summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>Tasks in queue order.</returns>
    /// <exception cref="ToolException">Missing or invalid file (exit code 3).</exception>
    public static IReadOnlyList<TaskSpec> Load(string path) =>
        File.Exists(path)
            ? Parse(File.ReadAllText(path), path)
            : throw new ToolException(ExitCodes.BadInput, $"tasks file '{path}' not found");

    /// <summary>Parses tasks JSON.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns>Tasks in queue order.</returns>
    /// <exception cref="ToolException">Invalid content (exit code 3).</exception>
    public static IReadOnlyList<TaskSpec> Parse(string json, string sourceName)
    {
        List<TaskDto?> raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<TaskDto?>>(json, Options) ?? throw Bad(sourceName, "invalid JSON: expected an array of tasks");
        }
        catch (JsonException e)
        {
            throw Bad(sourceName, "invalid JSON: " + e.Message);
        }

        var tasks = new List<TaskSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            var dto = raw[i] ?? throw Bad(sourceName, $"task #{i + 1}: must be an object");
            var id = dto.Id ?? throw Bad(sourceName, $"task #{i + 1}: missing 'id'");
            if (!SafeName.IsValid(id))
            {
                throw Bad(sourceName, $"task id '{id}' is not a safe name ({SafeName.Description})");
            }

            if (!seen.Add(id))
            {
                throw Bad(sourceName, $"duplicate task id '{id}' (ids are compared case-insensitively)");
            }

            var branch = dto.Branch ?? throw Bad(sourceName, $"task '{id}': missing 'branch'");
            if (!IsPlausibleBranch(branch))
            {
                throw Bad(sourceName, $"task '{id}': branch '{branch}' is not a valid branch name");
            }

            var deps = dto.DependsOn ?? new List<string>();
            foreach (var dep in deps)
            {
                if (!tasks.Any(t => string.Equals(t.Id, dep, StringComparison.Ordinal)))
                {
                    throw Bad(sourceName, $"task '{id}': dependsOn '{dep}' must name an earlier task in the file");
                }
            }

            tasks.Add(new TaskSpec(id, branch, deps));
        }

        return tasks;
    }

    static bool IsPlausibleBranch(string b) =>
        b.Length > 0
        && !b.StartsWith('-')
        && !b.EndsWith('/')
        && !b.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !b.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && !BadBranchParts.Any(p => b.Contains(p, StringComparison.Ordinal));

    static ToolException Bad(string source, string message) => new(ExitCodes.BadInput, $"{source}: {message}", "see docs/batch-tools.md#tasks-file");

    sealed class TaskDto
    {
        public string? Id { get; set; }

        public string? Branch { get; set; }

        public List<string>? DependsOn { get; set; }

        // Accepted for compatibility and ignored: touches are derived from git.
        public JsonElement? Touches { get; set; }

        public string? Title { get; set; }
    }
}
```

- [ ] **Step 4: Implement units and planner**

`src/Swarm.Batching/TaskUnits.cs`:

```csharp
namespace Swarm.Batching;

/// <summary>Tasks that land together: a single task, or a stack connected by <c>dependsOn</c>.</summary>
/// <param name="Members">Members in queue order.</param>
public sealed record TaskUnit(IReadOnlyList<TaskSpec> Members)
{
    /// <summary>Gets the first member's id.</summary>
    public string Id => Members[0].Id;

    /// <summary>Gets the number of tasks.</summary>
    public int Size => Members.Count;

    /// <summary>Gets the member ids in order.</summary>
    public IEnumerable<string> Ids => Members.Select(m => m.Id);
}

/// <summary>Builds land units from tasks.</summary>
public static class TaskUnits
{
    /// <summary>Groups tasks connected by <c>dependsOn</c> (ids not in the list are ignored).</summary>
    /// <param name="tasks">Tasks in queue order.</param>
    /// <returns>Units ordered by their first member's position.</returns>
    public static IReadOnlyList<TaskUnit> Build(IReadOnlyList<TaskSpec> tasks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            index[tasks[i].Id] = i;
        }

        // Union-find whose roots are always the smallest index in the set.
        var parent = Enumerable.Range(0, tasks.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        for (var i = 0; i < tasks.Count; i++)
        {
            foreach (var dep in tasks[i].DependsOn)
            {
                if (index.TryGetValue(dep, out var j))
                {
                    var (a, b) = (Find(i), Find(j));
                    parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
        }

        return Enumerable.Range(0, tasks.Count)
            .GroupBy(Find)
            .OrderBy(g => g.Key)
            .Select(g => new TaskUnit(g.Order().Select(i => tasks[i]).ToList()))
            .ToList();
    }
}
```

`src/Swarm.Batching/BatchPlanner.cs`:

```csharp
namespace Swarm.Batching;

/// <summary>Pure batch planning: adaptive size, pre-batching, bisect halves.</summary>
public static class BatchPlanner
{
    /// <summary>Adapts the batch size: doubles after green, halves after red, within bounds.</summary>
    /// <param name="current">Current size.</param>
    /// <param name="red">Whether the batch had any red.</param>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <returns>The next size.</returns>
    public static int NextSize(int current, bool red, int min, int max) =>
        red ? Math.Max(current / 2, min) : Math.Min(current * 2, max);

    /// <summary>Picks the next batch from the queue.</summary>
    /// <param name="queue">Units in queue order.</param>
    /// <param name="size">Target number of tasks.</param>
    /// <param name="overlaps">Whether two units change a common file.</param>
    /// <param name="prebatch">Whether overlapping units are kept apart.</param>
    /// <returns>The picked units (never empty for a non-empty queue: the head is always taken).</returns>
    public static IReadOnlyList<TaskUnit> PreBatch(IReadOnlyList<TaskUnit> queue, int size, Func<TaskUnit, TaskUnit, bool> overlaps, bool prebatch)
    {
        var pick = new List<TaskUnit>();
        var taken = 0;
        foreach (var unit in queue)
        {
            if (pick.Count > 0 && taken + unit.Size > size)
            {
                continue;
            }

            if (prebatch && pick.Any(p => overlaps(p, unit)))
            {
                continue;
            }

            pick.Add(unit);
            taken += unit.Size;
            if (taken >= size)
            {
                break;
            }
        }

        return pick;
    }

    /// <summary>Splits a red set for bisect (units are never split).</summary>
    /// <param name="units">At least two units.</param>
    /// <returns>Left (the first Count/2 units) and right.</returns>
    public static (IReadOnlyList<TaskUnit> Left, IReadOnlyList<TaskUnit> Right) Halve(IReadOnlyList<TaskUnit> units)
    {
        var half = units.Count / 2;
        return (units.Take(half).ToList(), units.Skip(half).ToList());
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~TasksFileTests|FullyQualifiedName~BatchPlannerTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "Add tasks file, stack units and batch planner" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 10: Integration worktree, derived touches, epic checks, and the `ILander` contract

**Files:**
- Create: `src/Swarm.Batching/TouchIndex.cs`, `src/Swarm.Batching/IntegrationWorktree.cs`, `src/Swarm.Batching/RepoChecks.cs`, `src/Swarm.Batching/Landing.cs`
- Test: `tests/Swarm.Tools.Tests/Batching/IntegrationWorktreeTests.cs`, `tests/Swarm.Tools.Tests/Batching/TouchIndexTests.cs`

**Interfaces:**
- Consumes: `GitRunner`, `SharedFile`, `TextLines`, `ToolException` (Tasks 1, 3); `StatePaths.Guard` (Task 4); `TaskSpec`, `TaskUnit` (Task 9); `TempRepo` (Task 2).
- Produces (namespace `Swarm.Batching`):
  - `sealed class TouchIndex(GitRunner repo)` with `IReadOnlySet<string> Derive(string fromRef, string branchRef)` (`git diff --name-only from...branch`, case-insensitive set), `void Set(string taskId, IReadOnlySet<string> files)`, `IReadOnlySet<string> Get(string taskId)`, `bool Overlaps(TaskUnit a, TaskUnit b)`, `IReadOnlyList<string> Partners(IEnumerable<string> candidateIds, IReadOnlyCollection<string> files)` (distinct candidates whose touches meet `files`), `IReadOnlyDictionary<string, IReadOnlyList<string>> Snapshot()`.
  - `static class RepoChecks { static string EnsureEpic(GitRunner repo, string epicBranch); }` — returns the epic tip sha; missing -> `ToolException(BadInput, "epic branch '<b>' not found", "create it first, e.g. git branch <b> main")`; checked out in any worktree -> `ToolException(BadInput, "epic branch '<b>' is checked out in '<path>'", ...)`.
  - `sealed record UnitConflict(TaskUnit Unit, TaskSpec Offender, IReadOnlyList<string> Files, string GitOutput, IReadOnlyList<string> MergedBefore)`; `sealed record IntegrationResult(string Head, IReadOnlyList<TaskUnit> Merged, IReadOnlyList<UnitConflict> Conflicts)`; `sealed record RebaseOutcome(bool Clean, string Output)`.
  - `sealed class IntegrationWorktree(GitRunner repo, string worktreePath)` with `string WorktreePath` (guarded), `GitRunner Git` (tool identity, at the worktree), `void Ensure(string epicBranch)` (create detached at the epic, or reuse: prune, remove stale `index.lock`/`HEAD.lock`, abort leftover merge/rebase, reset), `void ResetTo(string commitish)` (`checkout -f --detach` + `clean -fd`), `IntegrationResult Integrate(string epicTip, IReadOnlyList<TaskUnit> units)` (sequential `merge --no-ff`, stop-on-conflict per unit: abort, roll the whole unit back, record, continue with the next unit), `RebaseOutcome RebaseCopy(TaskSpec task, string epicTip, string copyBranch)` (`checkout -B copy <task branch>`, `rebase <tip>`; conflict -> abort and delete the copy; the worker's branch is never touched).
  - **Lander contract (Plan B implements another `ILander`):**
    - `sealed record LandTask(string Id, string Branch, IReadOnlyList<string> DependsOn)`
    - `sealed record LandRequest(GitRunner Repo, GitRunner Worktree, string Epic, string EpicBranch, string EpicTipBefore, string TestedCommit, IReadOnlyList<LandTask> Tasks, int Batch, string RunId)`
    - `sealed record LandedTask(string TaskId, string Commit)`; `sealed record LandFailure(string TaskId, IReadOnlyList<string> Files, string GitOutput)`
    - `sealed record LandResult(string EpicTipAfter, IReadOnlyList<LandedTask> Landed, LandFailure? Failure, IReadOnlyList<string> NotAttempted)`
    - `interface ILander { string Name { get; } LandResult Land(LandRequest request); }`
    - Rules (batch enforces 4-6 and fails exit 4 otherwise): (1) called only after the suite was green on `TestedCommit` = `EpicTipBefore` + `merge --no-ff` of every task in `Tasks` order (stacks contiguous, in order); (2) land in `Tasks` order, stop at the first task that cannot land, report it as `Failure`, list every later task in `NotAttempted`; (3) move `refs/heads/<EpicBranch>` only from `EpicTipBefore` (compare-and-swap) and never touch task branches; `Worktree` is the idle integration worktree, which the lander may check out detached anywhere (batch resets it afterwards); (4) `EpicTipAfter` equals the epic ref after the call; (5) `Landed` + `Failure` + `NotAttempted` cover exactly the request's task ids; (6) when nothing failed, the tree of `EpicTipAfter` equals the tree of `TestedCommit`.
    - `sealed class FastForwardLander : ILander` (`Name = "fast-forward"`; `update-ref` the epic from `EpicTipBefore` to `TestedCommit`; CAS failure -> `ToolException(Environment, "epic branch '<b>' moved during the run; nothing landed for batch <n>")`).

- [ ] **Step 1: Write the failing tests**

`tests/Swarm.Tools.Tests/Batching/TouchIndexTests.cs`:

```csharp
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
```

`tests/Swarm.Tools.Tests/Batching/IntegrationWorktreeTests.cs`:

```csharp
using Swarm.Batching;
using Swarm.Git;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class IntegrationWorktreeTests
{
    static TaskSpec T(string id, params string[] deps) => new(id, "task/" + id, deps);

    static (TempRepo Repo, GitRunner Git, IntegrationWorktree Wt) Setup()
    {
        var repo = TempRepo.Create();
        repo.Commit("shared", ("shared.txt", "base\n"));
        repo.Epic();
        var git = new GitRunner(repo.Root);
        var wt = new IntegrationWorktree(git, Path.Combine(repo.WorktreeRoot, "int-E1"));
        wt.Ensure("epic/E1");
        return (repo, git, wt);
    }

    static string Status(IntegrationWorktree wt) => wt.Git.Run("status", "--porcelain");

    [Fact]
    public void Integrate_MergesCleanTasksInOrder()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var r = wt.Integrate(git.RevParse("refs/heads/epic/E1"), TaskUnits.Build([T("T1"), T("T2")]));
        Assert.Equal(2, r.Merged.Count);
        Assert.Empty(r.Conflicts);
        Assert.True(repo.HasFile(r.Head, "one.txt") && repo.HasFile(r.Head, "two.txt"));
    }

    [Fact]
    public void Integrate_StopsOnConflictReturnsOffenderAndContinues()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        repo.Branch("task/T3", "epic/E1", ("three.txt", "3\n"));
        var r = wt.Integrate(git.RevParse("refs/heads/epic/E1"), TaskUnits.Build([T("T1"), T("T2"), T("T3")]));
        Assert.Equal(new[] { "T1", "T3" }, r.Merged.Select(u => u.Id));
        var c = Assert.Single(r.Conflicts);
        Assert.Equal("T2", c.Offender.Id);
        Assert.Equal(new[] { "shared.txt" }, c.Files);
        Assert.Equal(new[] { "T1" }, c.MergedBefore);
        Assert.Contains("CONFLICT", c.GitOutput);
        Assert.Empty(Status(wt));
    }

    [Fact]
    public void Integrate_RollsBackAWholeStackOnConflict()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T0", "epic/E1", ("shared.txt", "zero\n"));
        repo.Branch("task/T1", "epic/E1", ("s1.txt", "1\n"));
        repo.Branch("task/T2", "task/T1", ("shared.txt", "two\n"));
        var tip = git.RevParse("refs/heads/epic/E1");
        var r = wt.Integrate(tip, TaskUnits.Build([T("T0"), T("T1"), T("T2", "T1")]));
        Assert.Equal(new[] { "T0" }, r.Merged.Select(u => u.Id));
        Assert.Equal(new[] { "T1", "T2" }, Assert.Single(r.Conflicts).Unit.Ids);
        Assert.False(repo.HasFile(r.Head, "s1.txt"));
    }

    [Fact]
    public void Ensure_CleansIndexLockAndLeftoverMerge()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        wt.Git.Run("merge", "--no-edit", "refs/heads/task/T1");
        Assert.NotEqual(0, wt.Git.Try("merge", "--no-edit", "refs/heads/task/T2").ExitCode);
        File.WriteAllText(Path.Combine(wt.Git.Run("rev-parse", "--absolute-git-dir"), "index.lock"), "");
        new IntegrationWorktree(git, wt.WorktreePath).Ensure("epic/E1");
        Assert.Empty(Status(wt));
        Assert.Equal(git.RevParse("refs/heads/epic/E1"), wt.Git.RevParse("HEAD"));
    }

    [Fact]
    public void Ensure_ForeignDirectory_IsEnvironmentError()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        var path = Path.Combine(repo.WorktreeRoot, "int-E1");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mine.txt"), "x");
        var e = Assert.Throws<ToolException>(() => new IntegrationWorktree(new GitRunner(repo.Root), path).Ensure("epic/E1"));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
    }

    [Fact]
    public void Constructor_TooLongPath_IsUsage()
    {
        using var repo = TempRepo.Create();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new IntegrationWorktree(new GitRunner(repo.Root), Path.Combine(repo.Sandbox, new string('w', 220)))).ExitCode);
    }

    [Fact]
    public void RebaseCopy_Clean_LeavesWorkerBranchIntact()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var worker = repo.Sha("task/T2");
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic moves", ("other.txt", "o\n"));
        repo.Git("checkout", "-q", "main");
        var tip = git.RevParse("refs/heads/epic/E1");
        var outcome = wt.RebaseCopy(T("T2"), tip, "rebased/E1/T2");
        Assert.True(outcome.Clean);
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.Equal(0, git.Try("merge-base", "--is-ancestor", tip, "refs/heads/rebased/E1/T2").ExitCode);
    }

    [Fact]
    public void RebaseCopy_Conflict_DeletesCopy()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        repo.Git("checkout", "-q", "epic/E1");
        repo.Commit("epic edits shared", ("shared.txt", "epic\n"));
        repo.Git("checkout", "-q", "main");
        var outcome = wt.RebaseCopy(T("T2"), git.RevParse("refs/heads/epic/E1"), "rebased/E1/T2");
        Assert.False(outcome.Clean);
        Assert.False(git.RefExists("refs/heads/rebased/E1/T2"));
        Assert.Empty(Status(wt));
    }

    [Fact]
    public void EnsureEpic_MissingOrCheckedOut_IsBadInput()
    {
        using var repo = TempRepo.Create();
        var git = new GitRunner(repo.Root);
        Assert.Contains("not found", Assert.Throws<ToolException>(() => RepoChecks.EnsureEpic(git, "epic/E1")).Message);
        repo.Epic();
        Assert.Equal(repo.Sha("epic/E1"), RepoChecks.EnsureEpic(git, "epic/E1"));
        repo.Git("worktree", "add", "-q", Path.Combine(repo.Sandbox, "epicwt"), "epic/E1");
        var e = Assert.Throws<ToolException>(() => RepoChecks.EnsureEpic(git, "epic/E1"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("is checked out in", e.Message);
    }

    [Fact]
    public void FastForwardLander_MovesEpicToTestedCommitWithCas()
    {
        var (repo, git, wt) = Setup();
        using var _ = repo;
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        var tip = git.RevParse("refs/heads/epic/E1");
        var head = wt.Integrate(tip, TaskUnits.Build([T("T1")])).Head;
        var request = new LandRequest(git, wt.Git, "E1", "epic/E1", tip, head, [new LandTask("T1", "task/T1", [])], 1, "run1");
        var result = new FastForwardLander().Land(request);
        Assert.Equal(head, result.EpicTipAfter);
        Assert.Equal(head, git.RevParse("refs/heads/epic/E1"));
        Assert.Equal("T1", Assert.Single(result.Landed).TaskId);
        var e = Assert.Throws<ToolException>(() => new FastForwardLander().Land(request));
        Assert.Contains("moved during the run", e.Message);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~IntegrationWorktreeTests|FullyQualifiedName~TouchIndexTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `TouchIndex` and `RepoChecks`**

`src/Swarm.Batching/TouchIndex.cs`:

```csharp
using Swarm.Git;

namespace Swarm.Batching;

/// <summary>Files each task changes, derived from git (never trusted from the tasks file).</summary>
/// <param name="repo">Runner in the main worktree.</param>
public sealed class TouchIndex(GitRunner repo)
{
    static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, IReadOnlySet<string>> touches = new(StringComparer.Ordinal);

    /// <summary>Derives changed files: <c>git diff --name-only from...branch</c> (since the merge base).</summary>
    /// <param name="fromRef">Usually the epic tip.</param>
    /// <param name="branchRef">The task branch ref.</param>
    /// <returns>Case-insensitive set of repo-relative paths.</returns>
    public IReadOnlySet<string> Derive(string fromRef, string branchRef) =>
        repo.Lines("diff", "--name-only", $"{fromRef}...{branchRef}").ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a task's files.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="files">Its files.</param>
    public void Set(string taskId, IReadOnlySet<string> files) => touches[taskId] = files;

    /// <summary>Gets a task's files.</summary>
    /// <param name="taskId">Task id.</param>
    /// <returns>The files, or an empty set.</returns>
    public IReadOnlySet<string> Get(string taskId) => touches.TryGetValue(taskId, out var f) ? f : None;

    /// <summary>Checks whether two units change a common file.</summary>
    /// <param name="a">First unit.</param>
    /// <param name="b">Second unit.</param>
    /// <returns>True on overlap.</returns>
    public bool Overlaps(TaskUnit a, TaskUnit b) => a.Ids.Any(x => b.Ids.Any(y => Get(x).Overlaps(Get(y))));

    /// <summary>Finds which candidate tasks change any of the given files.</summary>
    /// <param name="candidateIds">Tasks already in the tested state.</param>
    /// <param name="files">Conflicting files.</param>
    /// <returns>Distinct matching ids in candidate order.</returns>
    public IReadOnlyList<string> Partners(IEnumerable<string> candidateIds, IReadOnlyCollection<string> files) =>
        candidateIds.Distinct(StringComparer.Ordinal).Where(id => Get(id).Overlaps(files)).ToList();

    /// <summary>Copies the index for the summary.</summary>
    /// <returns>Task id to sorted files.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Snapshot() =>
        touches.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
}
```

`src/Swarm.Batching/RepoChecks.cs`:

```csharp
using Swarm.Git;

namespace Swarm.Batching;

/// <summary>Pre-run repository checks.</summary>
public static class RepoChecks
{
    /// <summary>Requires the epic branch to exist and not be checked out anywhere (batch moves it by ref).</summary>
    /// <param name="repo">Runner in the main worktree.</param>
    /// <param name="epicBranch">Epic branch name.</param>
    /// <returns>The epic tip sha.</returns>
    /// <exception cref="ToolException">Missing or checked out (exit code 3).</exception>
    public static string EnsureEpic(GitRunner repo, string epicBranch)
    {
        var fullRef = "refs/heads/" + epicBranch;
        if (!repo.RefExists(fullRef))
        {
            throw new ToolException(ExitCodes.BadInput, $"epic branch '{epicBranch}' not found", $"create it first, e.g. git branch {epicBranch} main");
        }

        string? current = null;
        foreach (var line in repo.Lines("worktree", "list", "--porcelain"))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line["worktree ".Length..];
            }
            else if (line == "branch " + fullRef)
            {
                throw new ToolException(ExitCodes.BadInput, $"epic branch '{epicBranch}' is checked out in '{current}'", "batch moves the epic by ref; check out another branch there");
            }
        }

        return repo.RevParse(fullRef);
    }
}
```

- [ ] **Step 4: Implement `IntegrationWorktree`** (`src/Swarm.Batching/IntegrationWorktree.cs`)

```csharp
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Batching;

/// <summary>A unit that could not be merged (or landed).</summary>
/// <param name="Unit">The whole unit (all members go back).</param>
/// <param name="Offender">The member whose merge failed.</param>
/// <param name="Files">Conflicting files.</param>
/// <param name="GitOutput">Git's output.</param>
/// <param name="MergedBefore">Task ids already in the tested state when it failed.</param>
public sealed record UnitConflict(TaskUnit Unit, TaskSpec Offender, IReadOnlyList<string> Files, string GitOutput, IReadOnlyList<string> MergedBefore);

/// <summary>Result of building an integration state.</summary>
/// <param name="Head">Integration commit (epic tip plus merged units).</param>
/// <param name="Merged">Units merged, in order.</param>
/// <param name="Conflicts">Units returned.</param>
public sealed record IntegrationResult(string Head, IReadOnlyList<TaskUnit> Merged, IReadOnlyList<UnitConflict> Conflicts);

/// <summary>Result of rebasing a copy ref.</summary>
/// <param name="Clean">True when the rebase succeeded.</param>
/// <param name="Output">Git's output.</param>
public sealed record RebaseOutcome(bool Clean, string Output);

/// <summary>The tool-owned, detached integration worktree of one epic (also used for rebases).</summary>
public sealed class IntegrationWorktree
{
    readonly GitRunner repo;

    /// <summary>Initializes a new instance of the <see cref="IntegrationWorktree"/> class.</summary>
    /// <param name="repo">Runner in the main worktree.</param>
    /// <param name="worktreePath">Absolute worktree path.</param>
    /// <exception cref="ToolException">Path too long (exit code 2).</exception>
    public IntegrationWorktree(GitRunner repo, string worktreePath)
    {
        this.repo = repo;
        WorktreePath = StatePaths.Guard(Path.GetFullPath(worktreePath), "integration worktree");
        Git = repo.At(WorktreePath).WithIdentity();
    }

    /// <summary>Gets the worktree path.</summary>
    public string WorktreePath { get; }

    /// <summary>Gets a runner in the worktree that commits with the tool identity.</summary>
    public GitRunner Git { get; }

    /// <summary>Creates the worktree detached at the epic, or reuses and cleans an existing one.</summary>
    /// <param name="epicBranch">Epic branch.</param>
    /// <exception cref="ToolException">The path is a non-empty directory that is not a worktree of this repo, or git fails (exit code 4).</exception>
    public void Ensure(string epicBranch)
    {
        repo.Run("worktree", "prune");
        if (!IsRegistered())
        {
            if (Directory.Exists(WorktreePath) && Directory.EnumerateFileSystemEntries(WorktreePath).Any())
            {
                throw new ToolException(ExitCodes.Environment, $"'{WorktreePath}' exists but is not a worktree of this repository", "remove it or set worktreeRoot in .swarm/batch.json");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(WorktreePath)!);
            repo.Run("worktree", "add", "-q", "--detach", WorktreePath, "refs/heads/" + epicBranch);
            return;
        }

        // Leftovers from a crashed run. Safe: only this tool uses the worktree and the caller holds the epic's batch lock.
        var gitDir = Git.Run("rev-parse", "--absolute-git-dir");
        foreach (var name in new[] { "index.lock", "HEAD.lock" })
        {
            var file = Path.Combine(gitDir, name);
            if (File.Exists(file))
            {
                SharedFile.Retry(() => File.Delete(file));
            }
        }

        Git.Try("merge", "--abort");
        Git.Try("rebase", "--abort");
        ResetTo("refs/heads/" + epicBranch);
    }

    /// <summary>Detaches at a commit and removes untracked files (ignored build output is kept warm).</summary>
    /// <param name="commitish">Target.</param>
    public void ResetTo(string commitish)
    {
        Git.Run("checkout", "-q", "-f", "--detach", commitish);
        Git.Run("clean", "-q", "-fd");
    }

    /// <summary>Builds epic tip + sequential <c>merge --no-ff</c> of each unit; a conflicting unit is rolled back and skipped.</summary>
    /// <param name="epicTip">Starting commit.</param>
    /// <param name="units">Units in order.</param>
    /// <returns>The integration result.</returns>
    public IntegrationResult Integrate(string epicTip, IReadOnlyList<TaskUnit> units)
    {
        ResetTo(epicTip);
        var merged = new List<TaskUnit>();
        var conflicts = new List<UnitConflict>();
        foreach (var unit in units)
        {
            var before = Git.RevParse("HEAD");
            UnitConflict? conflict = null;
            foreach (var task in unit.Members)
            {
                var r = Git.Try("merge", "--no-ff", "--no-edit", "-m", $"batch: merge {task.Id} ({task.Branch})", task.BranchRef);
                if (r.ExitCode == 0)
                {
                    continue;
                }

                var files = Git.Lines("diff", "--name-only", "--diff-filter=U");
                Git.Try("merge", "--abort");
                conflict = new UnitConflict(unit, task, files, (r.StdOut + r.StdErr).Trim(), merged.SelectMany(u => u.Ids).ToList());
                break;
            }

            if (conflict is null)
            {
                merged.Add(unit);
            }
            else
            {
                ResetTo(before);
                conflicts.Add(conflict);
            }
        }

        return new IntegrationResult(Git.RevParse("HEAD"), merged, conflicts);
    }

    /// <summary>Rebases a COPY of the task branch onto the epic tip; the worker's branch is never modified.</summary>
    /// <param name="task">The task (its <see cref="TaskSpec.Branch"/> is the worker's branch).</param>
    /// <param name="epicTip">Rebase target.</param>
    /// <param name="copyBranch">Copy ref name, e.g. <c>rebased/E1/T2</c>.</param>
    /// <returns>Clean or not, with git's output; on conflict the copy is deleted.</returns>
    public RebaseOutcome RebaseCopy(TaskSpec task, string epicTip, string copyBranch)
    {
        ResetTo(epicTip);
        Git.Run("checkout", "-q", "-f", "-B", copyBranch, task.BranchRef);
        var r = Git.Try("rebase", epicTip);
        var output = (r.StdOut + r.StdErr).Trim();
        if (r.ExitCode != 0)
        {
            Git.Try("rebase", "--abort");
            ResetTo(epicTip);
            Git.Try("branch", "-D", copyBranch);
            return new RebaseOutcome(false, output);
        }

        // Detach so the copy branch is not checked out here.
        ResetTo(epicTip);
        return new RebaseOutcome(true, output);
    }

    bool IsRegistered()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return repo.Lines("worktree", "list", "--porcelain")
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Any(l => string.Equals(Path.GetFullPath(l["worktree ".Length..]).TrimEnd('\\', '/'), WorktreePath.TrimEnd('\\', '/'), comparison));
    }
}
```

- [ ] **Step 5: Implement the lander contract** (`src/Swarm.Batching/Landing.cs`)

```csharp
using Swarm.Git;

namespace Swarm.Batching;

/// <summary>A task to land.</summary>
/// <param name="Id">Task id.</param>
/// <param name="Branch">Branch that was tested (worker branch or rebased copy).</param>
/// <param name="DependsOn">Stack dependencies (all earlier in the same request).</param>
public sealed record LandTask(string Id, string Branch, IReadOnlyList<string> DependsOn);

/// <summary>Everything a lander needs. <c>TestedCommit</c> = <c>EpicTipBefore</c> + <c>merge --no-ff</c> of every task in order, and it was green.</summary>
/// <param name="Repo">Runner in the main worktree.</param>
/// <param name="Worktree">Runner in the idle integration worktree (tool identity); may be checked out detached anywhere.</param>
/// <param name="Epic">Epic id (for trailers).</param>
/// <param name="EpicBranch">Epic branch to move.</param>
/// <param name="EpicTipBefore">Expected current epic tip (compare-and-swap).</param>
/// <param name="TestedCommit">The green integration commit.</param>
/// <param name="Tasks">Tasks in landing order; stacks are contiguous and in order.</param>
/// <param name="Batch">Batch number (for trailers).</param>
/// <param name="RunId">Run id (for trailers and reflog).</param>
public sealed record LandRequest(GitRunner Repo, GitRunner Worktree, string Epic, string EpicBranch, string EpicTipBefore, string TestedCommit, IReadOnlyList<LandTask> Tasks, int Batch, string RunId);

/// <summary>A landed task.</summary>
/// <param name="TaskId">Task id.</param>
/// <param name="Commit">Epic commit that contains it.</param>
public sealed record LandedTask(string TaskId, string Commit);

/// <summary>The first task that could not land.</summary>
/// <param name="TaskId">Task id.</param>
/// <param name="Files">Conflicting files, if any.</param>
/// <param name="GitOutput">Git's output.</param>
public sealed record LandFailure(string TaskId, IReadOnlyList<string> Files, string GitOutput);

/// <summary>What a lander did.</summary>
/// <param name="EpicTipAfter">Epic tip after landing (must equal the epic ref).</param>
/// <param name="Landed">Landed tasks, in order.</param>
/// <param name="Failure">The first task that failed, or null.</param>
/// <param name="NotAttempted">Every task after the failure, in order.</param>
public sealed record LandResult(string EpicTipAfter, IReadOnlyList<LandedTask> Landed, LandFailure? Failure, IReadOnlyList<string> NotAttempted);

/// <summary>
/// Lands a green, tested set of tasks on the epic branch. Rules: land in request order and stop at the first failure;
/// move the epic only from <see cref="LandRequest.EpicTipBefore"/> (compare-and-swap); never touch task branches;
/// account for every task exactly once; when nothing failed, the landed tree must equal the tested tree.
/// </summary>
public interface ILander
{
    /// <summary>Gets the lander name (recorded in summaries).</summary>
    string Name { get; }

    /// <summary>Lands the tasks.</summary>
    /// <param name="request">The request.</param>
    /// <returns>What landed.</returns>
    LandResult Land(LandRequest request);
}

/// <summary>Default lander until Plan B: fast-forwards the epic to the tested integration commit (one merge commit per task).</summary>
public sealed class FastForwardLander : ILander
{
    /// <inheritdoc/>
    public string Name => "fast-forward";

    /// <inheritdoc/>
    /// <exception cref="ToolException">The epic moved since the batch started (exit code 4).</exception>
    public LandResult Land(LandRequest request)
    {
        var r = request.Repo.Try("update-ref", "-m", $"batch {request.RunId}: land batch {request.Batch}", "refs/heads/" + request.EpicBranch, request.TestedCommit, request.EpicTipBefore);
        if (r.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Environment, $"epic branch '{request.EpicBranch}' moved during the run; nothing landed for batch {request.Batch}", "another writer updated the epic; re-run batch");
        }

        return new LandResult(request.TestedCommit, request.Tasks.Select(t => new LandedTask(t.Id, request.TestedCommit)).ToList(), null, []);
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~IntegrationWorktreeTests|FullyQualifiedName~TouchIndexTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Add integration worktree, derived touches, epic checks and ILander" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 11: Batch engine (batches, bisect, landing, returns, rebase-copy requeue, summary)

**Files:**
- Create: `src/Swarm.Batching/BatchEngine.cs`
- Test: `tests/Swarm.Tools.Tests/Batching/BatchEngineTests.cs`, `tests/Swarm.Tools.Tests/Batching/BatchReturnsTests.cs`, `tests/Swarm.Tools.Tests/Support/BatchScenario.cs`

**Interfaces:**
- Consumes: everything from Tasks 3-10: `GitRunner`, `RepoPaths`, `StatePaths`, `StateLayout`, `SwarmConfig`, `SlotSemaphore`, `SlotOptions`, `GateRunner`, `GateRequest`, `SwarmJson`, `EventLog`, `EventTypes`, `ReturnLedger`, `ReturnedEntry` + constants, `RunDirectories`, `Progress`, `BatchSummary` + records, `TasksFile`, `TaskSpec`, `TaskUnit`, `TaskUnits`, `BatchPlanner`, `TouchIndex`, `IntegrationWorktree`, `RepoChecks`, `ILander` + records.
- Produces (namespace `Swarm.Batching`):
  - `static class BatchModes { const string Batched = "batched"; const string Serial = "serial"; }`
  - `sealed record BatchRunOptions { required string TasksFile; required SwarmConfig Config; string? RunId; string Mode = "batched"; bool? Prebatch; int? FixedSize; }`
  - `sealed class BatchEngine(RepoPaths repo, BatchRunOptions options, ILander lander, Progress progress) { BatchSummary Run(CancellationToken cancellationToken = default); }` — runs once. Pre-run failures THROW `ToolException` and create nothing (order: mode/size checks, worktree path guard, tasks file, epic checks, batch lock); after the run dir exists, failures (gate timeout 5, git/lander/environment 4, cancel 4) end the run with a written `summary.json` whose `exitCode`/`note` say why. Writes `<state>/runs/<runId>/{summary.json, returned.jsonl, events.jsonl, logs/suite-NNN.log}`. Summary exit code: 0 all landed, 1 otherwise, or the in-run failure code.

- [ ] **Step 1: Write the scenario helper** (`tests/Swarm.Tools.Tests/Support/BatchScenario.cs`)

```csharp
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

public static class BatchScenario
{
    public static BatchSummary Run(TempRepo repo, string tasksFile, ILander? lander = null, Func<SwarmConfig, SwarmConfig>? tweak = null, bool? prebatch = null, string mode = BatchModes.Batched)
    {
        var config = TestConfig.For(repo);
        config = tweak?.Invoke(config) ?? config;
        var options = new BatchRunOptions { TasksFile = tasksFile, Config = config, Prebatch = prebatch, Mode = mode };
        return new BatchEngine(RepoLocator.Locate(repo.Root), options, lander ?? new FastForwardLander(), new Progress(TextWriter.Null, Verbosity.Quiet)).Run();
    }

    public static IReadOnlyDictionary<string, ReturnedEntry> Returned(BatchSummary s) => ReturnLedger.ReadLatest(s.ReturnedFile);
}

/// <summary>Lands like <see cref="FastForwardLander"/>, except that the first request containing <c>failTaskId</c> fails at that task.</summary>
public sealed class FailOnceLander(string failTaskId) : ILander
{
    bool failed;

    public string Name => "fail-once";

    public LandResult Land(LandRequest r)
    {
        var index = r.Tasks.ToList().FindIndex(t => t.Id == failTaskId);
        if (failed || index < 0)
        {
            return new FastForwardLander().Land(r);
        }

        failed = true;

        // The integration chain is first-parent: TestedCommit~k drops the last k task merges.
        var prefix = r.Repo.RevParse($"{r.TestedCommit}~{r.Tasks.Count - index}");
        if (prefix != r.EpicTipBefore)
        {
            r.Repo.Run("update-ref", "refs/heads/" + r.EpicBranch, prefix, r.EpicTipBefore);
        }

        return new LandResult(
            prefix,
            r.Tasks.Take(index).Select(t => new LandedTask(t.Id, prefix)).ToList(),
            new LandFailure(failTaskId, [], "simulated land failure"),
            r.Tasks.Skip(index + 1).Select(t => t.Id).ToList());
    }
}

/// <summary>Violates the lander contract: lands one commit short of the tested tree.</summary>
public sealed class ShortLander : ILander
{
    public string Name => "short";

    public LandResult Land(LandRequest r)
    {
        var target = r.Repo.RevParse(r.TestedCommit + "~1");
        if (target != r.EpicTipBefore)
        {
            r.Repo.Run("update-ref", "refs/heads/" + r.EpicBranch, target, r.EpicTipBefore);
        }

        return new LandResult(target, r.Tasks.Select(t => new LandedTask(t.Id, target)).ToList(), null, []);
    }
}
```

- [ ] **Step 2: Write the failing core tests** (`tests/Swarm.Tools.Tests/Batching/BatchEngineTests.cs`)

```csharp
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class BatchEngineTests
{
    static TempRepo Repo()
    {
        var repo = TempRepo.Create();
        repo.Epic();
        return repo;
    }

    static string FourTasks(TempRepo repo, Func<int, (string Path, string Content)>? fileFor = null)
    {
        for (var i = 1; i <= 4; i++)
        {
            repo.Branch($"task/T{i}", "epic/E1", fileFor?.Invoke(i) ?? ($"t{i}.txt", $"{i}\n"));
        }

        return repo.WriteTasks([.. Enumerable.Range(1, 4).Select(i => new TaskLine($"T{i}", $"task/T{i}"))]);
    }

    [Fact]
    public void AllGreen_LandsEverythingInOneSuite_Exit0()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.Equal(4, s.TasksLanded);
        Assert.Equal(1, s.FullSuiteRuns);
        Assert.Equal(new[] { 4 }, s.SizeTrace);
        Assert.All(Enumerable.Range(1, 4), i => Assert.True(repo.HasFile("epic/E1", $"t{i}.txt")));
        Assert.True(File.Exists(Path.Combine(repo.StateDir, "runs", s.RunId, "summary.json")));
        Assert.Contains("\"type\":\"run-end\"", File.ReadAllText(s.EventsFile));
    }

    [Fact]
    public void RedTask_IsBisectedWithInferenceAndReturned_Exit1()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo, i => i == 3 ? ("T3.fail", "") : ($"t{i}.txt", $"{i}\n")));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T4" }, s.Landed.Select(l => l.Id));
        Assert.Equal((4, 3, 1), (s.FullSuiteRuns, s.BisectRuns, s.InferredRedSkipped));
        Assert.Equal(FinalState.ReturnedRed, BatchScenario.Returned(s)["T3"].Final);
        Assert.False(repo.HasFile("epic/E1", "T3.fail"));
    }

    [Fact]
    public void InteractionPair_BlamesTheLaterTask()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo, i => i switch { 1 => ("pair.fail", "t3.txt\n"), _ => ($"t{i}.txt", $"{i}\n") });
        var s = BatchScenario.Run(repo, tasks);
        Assert.Equal(new[] { "T1", "T2", "T4" }, s.Landed.Select(l => l.Id));
        Assert.Equal(ReturnKind.Red, BatchScenario.Returned(s)["T3"].Kind);
    }

    [Fact]
    public void Stack_LandsAsOneUnitAndIsNeverSplit()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("s1.txt", "1\n"));
        repo.Branch("task/T2", "task/T1", ("s2.txt", "2\n"));
        repo.Branch("task/T3", "epic/E1", ("T3.fail", ""));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2", ["T1"]), new TaskLine("T3", "task/T3")));
        Assert.Equal(new[] { "T1", "T2" }, s.Landed.Select(l => l.Id));
        Assert.Equal(1, s.Landed.Select(l => l.Batch).Distinct().Count());
        Assert.Equal((2, 1), (s.FullSuiteRuns, s.InferredRedSkipped));
    }

    [Fact]
    public void SerialMode_RunsOneSuitePerTask()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), mode: BatchModes.Serial);
        Assert.Equal((4, 4), (s.FullSuiteRuns, s.TasksLanded));
        Assert.Equal(new[] { 1, 1, 1, 1 }, s.SizeTrace);
    }

    [Fact]
    public void EmptyTasks_Exit0WithoutWorktreeOrSuite()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, repo.WriteTasks());
        Assert.Equal((ExitCodes.Ok, 0, "empty batch"), (s.ExitCode, s.FullSuiteRuns, s.Note));
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, "int-E1")));
    }

    [Fact]
    public void GateTimeout_Exit5_SummaryWrittenWithUnprocessed()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("other suite");
        var s = BatchScenario.Run(repo, tasks, tweak: c => c with { MaxWaitSec = 1 });
        Assert.Equal(ExitCodes.GateTimeout, s.ExitCode);
        Assert.Contains("no test slot free", s.Note);
        Assert.Equal(4, s.Unprocessed.Count);
        Assert.True(File.Exists(Path.Combine(repo.StateDir, "runs", s.RunId, "summary.json")));
    }

    [Fact]
    public void ConcurrentRunOnSameEpic_FailsFast()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var layout = new StateLayout(repo.StateDir);
        using var held = new SlotSemaphore(layout.BatchLockDir("E1"), SlotOptions.From(TestConfig.For(repo))).Acquire("other batch run");
        var e = Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks));
        Assert.Equal(ExitCodes.Environment, e.ExitCode);
        Assert.Contains("another batch run holds epic 'E1'", e.Message);
        Assert.False(Directory.Exists(layout.RunsDir));
    }

    [Fact]
    public void SecondRun_ReusesIntegrationWorktreeWithStaleLock()
    {
        using var repo = Repo();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        Assert.Equal(ExitCodes.Ok, BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"))).ExitCode);
        var wt = Path.Combine(repo.WorktreeRoot, "int-E1");
        File.WriteAllText(Path.Combine(TempRepo.RunGit(wt, "rev-parse", "--absolute-git-dir"), "index.lock"), "");
        repo.Branch("task/T2", "epic/E1", ("two.txt", "2\n"));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T2", "task/T2")));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.True(repo.HasFile("epic/E1", "two.txt"));
    }

    [Fact]
    public void LongWorktreeRoot_Exit2_NothingCreated()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        var e = Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks, tweak: c => c with { WorktreeRoot = Path.Combine(repo.Sandbox, new string('w', 220)) }));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.False(Directory.Exists(repo.StateDir));
    }

    [Fact]
    public void EpicCheckedOut_Exit3()
    {
        using var repo = Repo();
        var tasks = FourTasks(repo);
        repo.Git("worktree", "add", "-q", Path.Combine(repo.Sandbox, "epicwt"), "epic/E1");
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => BatchScenario.Run(repo, tasks)).ExitCode);
    }

    [Fact]
    public void LanderTreeMismatch_Exit4()
    {
        using var repo = Repo();
        var s = BatchScenario.Run(repo, FourTasks(repo), lander: new ShortLander());
        Assert.Equal(ExitCodes.Environment, s.ExitCode);
        Assert.Contains("differs from the tested tree", s.Note);
    }
}
```

- [ ] **Step 3: Write the failing returns tests** (`tests/Swarm.Tools.Tests/Batching/BatchReturnsTests.cs`)

```csharp
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Batching;

public class BatchReturnsTests
{
    static TempRepo SharedFileRepo()
    {
        var repo = TempRepo.Create();
        repo.Commit("shared", ("shared.txt", "base\n"));
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("shared.txt", "one\n"));
        repo.Branch("task/T2", "epic/E1", ("shared.txt", "two\n"));
        return repo;
    }

    [Fact]
    public void MissingBranch_ReturnsTaskAndDependents_RunsRest_Exit1()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", ("one.txt", "1\n"));
        repo.Branch("task/T10", "epic/E1", ("ten.txt", "10\n"));
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T9", "task/T9"), new TaskLine("T10", "task/T10", ["T9"])));
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal(new[] { "T1" }, s.Landed.Select(l => l.Id));
        var returned = BatchScenario.Returned(s);
        Assert.Equal((ReturnKind.BadInput, FinalState.ReturnedBadInput), (returned["T9"].Kind, returned["T9"].Final));
        Assert.Equal((ReturnKind.Dependency, FinalState.BlockedByDependency), (returned["T10"].Kind, returned["T10"].Final));
        Assert.Equal(2, s.BadInput);
    }

    [Fact]
    public void ConflictInBatch_WithoutPrebatch_ReturnsOffenderAndNeedsWorkerAfterFailedRebase()
    {
        using var repo = SharedFileRepo();
        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), prebatch: false);
        Assert.Equal(ExitCodes.Returned, s.ExitCode);
        Assert.Equal((1, 1), (s.Batches, s.FullSuiteRuns));
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((ReturnKind.Conflict, ReturnStage.Merge), (t2.Kind, t2.Stage));
        Assert.Equal(new[] { "T1" }, t2.ConflictingWith);
        Assert.Equal(new[] { "shared.txt" }, t2.Files);
        Assert.Equal((RebaseState.Conflict, FinalState.NeedsWorker), (t2.Rebase, t2.Final));
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.False(new GitRunner(repo.Root).RefExists("refs/heads/rebased/E1/T2"));
        Assert.Equal(1, s.NeedsWorker);
    }

    [Fact]
    public void Prebatch_SeparatesSameFileTasks_ConflictIsAgainstLandedPartner()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")));
        Assert.Equal((2, 1), (s.Batches, s.FullSuiteRuns));
        Assert.Equal(new[] { "T1" }, BatchScenario.Returned(s)["T2"].ConflictingWith);
    }

    [Fact]
    public void LandFailure_RebasedCopyIsRequeuedAndLands_Exit0()
    {
        using var repo = TempRepo.Create();
        repo.Epic();
        for (var i = 1; i <= 3; i++)
        {
            repo.Branch($"task/T{i}", "epic/E1", ($"t{i}.txt", $"{i}\n"));
        }

        var worker = repo.Sha("task/T2");
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2"), new TaskLine("T3", "task/T3")), lander: new FailOnceLander("T2"));
        Assert.Equal(ExitCodes.Ok, s.ExitCode);
        Assert.Equal(new[] { "T1", "T2", "T3" }, s.Landed.Select(l => l.Id));
        Assert.Equal("rebased/E1/T2", s.Landed.Single(l => l.Id == "T2").Branch);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((ReturnStage.Land, RebaseState.Clean, FinalState.RebasedAndLanded), (t2.Stage, t2.Rebase, t2.Final));
        Assert.Equal("rebased/E1/T2", t2.RebasedBranch);
        Assert.Equal(1, s.RebasedAndLanded);
        Assert.Equal(worker, repo.Sha("task/T2"));
        Assert.Equal(2, s.FullSuiteRuns);
    }

    [Fact]
    public void ZeroRebaseAttempts_GoesStraightToNeedsWorker()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), tweak: c => c with { MaxRebaseAttempts = 0 }, prebatch: false);
        var t2 = BatchScenario.Returned(s)["T2"];
        Assert.Equal((RebaseState.Skipped, FinalState.NeedsWorker), (t2.Rebase, t2.Final));
    }

    [Fact]
    public void ReturnedFile_IsAppendOnlyJsonlWithSchemaVersion()
    {
        using var repo = SharedFileRepo();
        var s = BatchScenario.Run(repo, repo.WriteTasks(new TaskLine("T1", "task/T1"), new TaskLine("T2", "task/T2")), prebatch: false);
        var lines = File.ReadAllLines(s.ReturnedFile);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{\"schemaVersion\":1,", l));
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~BatchEngineTests|FullyQualifiedName~BatchReturnsTests"`
Expected: FAIL to compile (`BatchEngine`, `BatchRunOptions`, `BatchModes` not defined).

- [ ] **Step 5: Implement the engine** (`src/Swarm.Batching/BatchEngine.cs`)

```csharp
using System.Diagnostics;
using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Batching;

/// <summary>Batch modes.</summary>
public static class BatchModes
{
    /// <summary>Adaptive batches with pre-batching and bisect.</summary>
    public const string Batched = "batched";

    /// <summary>One task per suite: the baseline.</summary>
    public const string Serial = "serial";
}

/// <summary>Options for one batch run.</summary>
public sealed record BatchRunOptions
{
    /// <summary>Gets the absolute tasks file path.</summary>
    public required string TasksFile { get; init; }

    /// <summary>Gets the validated config (flags already applied).</summary>
    public required SwarmConfig Config { get; init; }

    /// <summary>Gets an explicit run id, or null for a timestamped one.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the mode (<see cref="BatchModes"/>).</summary>
    public string Mode { get; init; } = BatchModes.Batched;

    /// <summary>Gets a pre-batching override (null = config).</summary>
    public bool? Prebatch { get; init; }

    /// <summary>Gets an experimental fixed batch size (start = min = max), or null.</summary>
    public int? FixedSize { get; init; }
}

/// <summary>
/// Integrates task branches onto the epic in adaptive batches: sequential merge with stop-on-conflict, one full
/// suite per batch under a test slot, halving bisect on red, landing through an <see cref="ILander"/> on green,
/// and an automatic rebase of a copy ref for conflicting tasks.
/// </summary>
public sealed class BatchEngine
{
    readonly RepoPaths repo;
    readonly BatchRunOptions options;
    readonly SwarmConfig config;
    readonly ILander lander;
    readonly Progress progress;
    readonly GitRunner main;
    readonly StateLayout state;
    readonly string epicBranch;
    readonly TouchIndex touches;
    readonly Dictionary<string, TaskSpec> original = new(StringComparer.Ordinal);
    readonly List<string> landedIds = [];
    readonly List<LandedRecord> landed = [];
    readonly List<SuiteRecord> suites = [];
    readonly List<BatchLogEntry> batchLog = [];
    readonly List<int> sizeTrace = [];
    readonly Dictionary<string, int> rebaseAttempts = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, IReadOnlyList<string>> initialTouches = new Dictionary<string, IReadOnlyList<string>>();
    // Set in Run before first use (the engine runs once); null! keeps nullable flow analysis quiet.
    IntegrationWorktree integration = null!;
    GateRunner gate = null!;
    ReturnLedger ledger = null!;
    EventLog events = null!;
    string runId = "";
    string runDir = "";
    string lastSuiteLog = "";
    int suiteRuns;
    int bisectRuns;
    int inferred;
    int batchNo;
    long waitMs;
    long runMs;
    bool started;

    /// <summary>Initializes a new instance of the <see cref="BatchEngine"/> class.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="options">Run options.</param>
    /// <param name="lander">How green tasks land on the epic.</param>
    /// <param name="progress">Human progress (stderr).</param>
    /// <exception cref="ToolException">The state dir path is invalid (exit code 2).</exception>
    public BatchEngine(RepoPaths repo, BatchRunOptions options, ILander lander, Progress progress)
    {
        this.repo = repo;
        this.options = options;
        config = options.Config;
        this.lander = lander;
        this.progress = progress;
        main = new GitRunner(repo.MainWorktreeRoot);
        state = new StateLayout(StatePaths.Resolve(repo, config.StateDir));
        epicBranch = config.EpicBranch;
        touches = new TouchIndex(main);
    }

    sealed record SetOutcome(bool Red, bool CleanAndLanded);

    /// <summary>Runs the batch (once per engine).</summary>
    /// <param name="cancellationToken">Stops the run; the summary records exit 4.</param>
    /// <returns>The summary (also written to <c>summary.json</c>).</returns>
    /// <exception cref="ToolException">Pre-run failure: bad mode/size or path (2), tasks file or epic (3), concurrent run (4).</exception>
    public BatchSummary Run(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            throw new InvalidOperationException("a BatchEngine runs once");
        }

        started = true;
        var wall = Stopwatch.StartNew();

        // Pre-run checks: each failure throws before anything is created.
        if (options.Mode is not (BatchModes.Batched or BatchModes.Serial))
        {
            throw new ToolException(ExitCodes.Usage, $"unknown mode '{options.Mode}' (batched|serial)");
        }

        if (options.FixedSize is < 1 or > 64)
        {
            throw new ToolException(ExitCodes.Usage, $"fixed batch size must be between 1 and 64 (got {options.FixedSize})");
        }

        var integrationPath = StatePaths.Guard(Path.Combine(StatePaths.ResolveWorktreeRoot(repo, config.WorktreeRoot), "int-" + config.Epic), "integration worktree");
        var tasks = TasksFile.Load(options.TasksFile);
        RepoChecks.EnsureEpic(main, epicBranch);
        runId = options.RunId ?? RunDirectories.NewRunId(config.Epic, DateTime.UtcNow);
        var lockOptions = SlotOptions.From(config) with { Slots = 1, MaxWait = null };
        using var batchLock = new SlotSemaphore(state.BatchLockDir(config.Epic), lockOptions).TryAcquire($"batch run {runId}")
            ?? throw new ToolException(ExitCodes.Environment, $"another batch run holds epic '{config.Epic}'", $"wait for it to finish; lock in {state.BatchLockDir(config.Epic)}");

        RunDirectories.Prune(state, config.KeepRuns);
        runDir = RunDirectories.Create(state, runId);
        events = new EventLog(Path.Combine(runDir, "events.jsonl"), runId);
        ledger = new ReturnLedger(Path.Combine(runDir, "returned.jsonl"), runId);
        foreach (var t in tasks)
        {
            original[t.Id] = t;
        }

        events.Write(EventTypes.RunStart, new { tasks = tasks.Count, epic = config.Epic, epicBranch, mode = options.Mode, lander = lander.Name });
        if (tasks.Count == 0)
        {
            progress.Info("nothing to do: the tasks file is empty (0 tasks); no worktree created, no suite run");
            return Finish(wall, null, "empty batch");
        }

        try
        {
            Loop(Preflight(tasks), integrationPath, cancellationToken);
            return Finish(wall, null, null);
        }
        catch (ToolException e)
        {
            return Finish(wall, e.ExitCode, e.Summary);
        }
        catch (OperationCanceledException)
        {
            return Finish(wall, ExitCodes.Environment, "cancelled");
        }
    }

    static string Ids(IEnumerable<TaskUnit> units) => string.Join(',', units.SelectMany(u => u.Ids));

    IReadOnlyList<TaskSpec> Preflight(IReadOnlyList<TaskSpec> tasks)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<TaskSpec>();
        foreach (var t in tasks)
        {
            if (!main.RefExists(t.BranchRef))
            {
                dropped.Add(t.Id);
                progress.Info($"  RETURN {t.Id}: branch '{t.Branch}' not found");
                ledger!.Record(Entry(t, ReturnKind.BadInput, ReturnStage.Preflight, 0, $"task branch '{t.Branch}' not found") with { Final = FinalState.ReturnedBadInput });
                continue;
            }

            if (t.DependsOn.FirstOrDefault(dropped.Contains) is { } dep)
            {
                dropped.Add(t.Id);
                progress.Info($"  RETURN {t.Id}: depends on returned task {dep}");
                ledger!.Record(Entry(t, ReturnKind.Dependency, ReturnStage.Preflight, 0, $"depends on returned task '{dep}'") with { ConflictingWith = [dep], Final = FinalState.BlockedByDependency });
                continue;
            }

            ready.Add(t);
        }

        return ready;
    }

    void Loop(IReadOnlyList<TaskSpec> ready, string integrationPath, CancellationToken ct)
    {
        var queue = TaskUnits.Build(ready).ToList();
        if (queue.Count == 0)
        {
            return;
        }

        integration = new IntegrationWorktree(main, integrationPath);
        integration.Ensure(epicBranch);
        gate = new GateRunner(new SlotSemaphore(state.SlotsDir, SlotOptions.From(config)), line => progress.Detail("    " + line));
        var tip = main.RevParse("refs/heads/" + epicBranch);
        foreach (var t in ready)
        {
            touches.Set(t.Id, touches.Derive(tip, t.BranchRef));
        }

        initialTouches = touches.Snapshot();
        var (start, min, max) = options.Mode == BatchModes.Serial ? (1, 1, 1)
            : options.FixedSize is { } n ? (n, n, n)
            : (config.Batch.Start, config.Batch.Min, config.Batch.Max);
        var prebatch = options.Mode != BatchModes.Serial && (options.Prebatch ?? config.Prebatch);
        var size = start;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var pick = BatchPlanner.PreBatch(queue, size, touches.Overlaps, prebatch);
            foreach (var unit in pick)
            {
                queue.Remove(unit);
            }

            batchNo++;
            sizeTrace.Add(size);
            progress.Info($"batch {batchNo} size {size}: {Ids(pick)}");
            events!.Write(EventTypes.BatchStart, new { batch = batchNo, size, tasks = pick.SelectMany(u => u.Ids).ToList() });
            var outcome = ProcessSet(pick, batchNo, knownRed: false, bisect: false, queue, ct);
            RebaseReturned(queue);
            size = BatchPlanner.NextSize(size, outcome.Red, min, max);
        }

        integration.ResetTo("refs/heads/" + epicBranch);
    }

    SetOutcome ProcessSet(IReadOnlyList<TaskUnit> units, int batch, bool knownRed, bool bisect, List<TaskUnit> queue, CancellationToken ct)
    {
        var tip = main.RevParse("refs/heads/" + epicBranch);
        var integ = integration!.Integrate(tip, units);
        var mergedIds = integ.Merged.SelectMany(u => u.Ids).ToList();
        events!.Write(EventTypes.Merge, new { batch, merged = mergedIds, conflicts = integ.Conflicts.Select(c => c.Offender.Id).ToList(), head = integ.Head });
        foreach (var c in integ.Conflicts)
        {
            ReturnConflict(c, batch, ReturnStage.Merge);
        }

        if (integ.Merged.Count == 0)
        {
            batchLog.Add(new BatchLogEntry(batch, bisect, mergedIds, "all merges conflicted: no suite run"));
            return new SetOutcome(Red: false, CleanAndLanded: false);
        }

        bool red;
        if (knownRed)
        {
            red = true;
            inferred++;
        }
        else
        {
            red = Suite(batch, bisect, mergedIds, integ.Head, ct) != 0;
        }

        batchLog.Add(new BatchLogEntry(batch, bisect, mergedIds, knownRed ? "red (inferred)" : red ? "red" : "green"));
        if (!red)
        {
            return new SetOutcome(false, LandSet(integ, tip, batch, queue) && integ.Conflicts.Count == 0);
        }

        if (integ.Merged.Count == 1)
        {
            RejectRed(integ.Merged[0], batch);
            return new SetOutcome(true, false);
        }

        var (left, right) = BatchPlanner.Halve(integ.Merged);
        events.Write(EventTypes.Bisect, new { batch, left = left.SelectMany(u => u.Ids).ToList(), right = right.SelectMany(u => u.Ids).ToList() });
        var l = ProcessSet(left, batch, knownRed: false, bisect: true, queue, ct);

        // Left green and fully landed: epic tip + right is exactly the state already seen red, so infer instead of re-running.
        ProcessSet(right, batch, knownRed: !l.Red && l.CleanAndLanded, bisect: true, queue, ct);
        return new SetOutcome(true, false);
    }

    int Suite(int batch, bool bisect, IReadOnlyList<string> ids, string head, CancellationToken ct)
    {
        suiteRuns++;
        if (bisect)
        {
            bisectRuns++;
        }

        var label = $"b{batch}-{(bisect ? "bisect" : "batch")}-{suiteRuns:000}";
        var run = gate!.Run(new GateRequest(config.TestCommand, integration!.WorktreePath, label), ct);
        lastSuiteLog = Path.Combine(runDir, "logs", $"suite-{suiteRuns:000}.log");
        File.WriteAllText(lastSuiteLog, run.Log);
        suites.Add(new SuiteRecord(run.Result, lastSuiteLog, ids));
        waitMs += run.Result.WaitMs;
        runMs += run.Result.RunMs;
        var verdict = run.Result.ExitCode == 0 ? "green" : "red";
        events!.Write(EventTypes.Suite, new { label, tasks = ids, head, verdict, run.Result.ExitCode, run.Result.WaitMs, run.Result.RunMs, log = lastSuiteLog });
        progress.Info($"  suite {label} [{string.Join(',', ids)}]: {verdict} ({run.Result.RunMs} ms, waited {run.Result.WaitMs} ms)");
        return run.Result.ExitCode;
    }

    bool LandSet(IntegrationResult integ, string tip, int batch, List<TaskUnit> queue)
    {
        var members = integ.Merged.SelectMany(u => u.Members).ToList();
        var byId = members.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var request = new LandRequest(main, integration!.Git, config.Epic, epicBranch, tip, integ.Head, members.Select(t => new LandTask(t.Id, t.Branch, t.DependsOn)).ToList(), batch, runId);
        var result = lander.Land(request);
        Verify(request, result);
        foreach (var l in result.Landed)
        {
            MarkLanded(byId[l.TaskId], batch, l.Commit);
        }

        var returnedHere = new HashSet<string>(StringComparer.Ordinal);
        if (result.Failure is { } failure)
        {
            // The failed task's unrelanded stack mates go back with it (a stack lands as one unit).
            var unit = integ.Merged.First(u => u.Ids.Contains(failure.TaskId));
            var rest = unit.Members.Where(m => !landedIds.Contains(m.Id)).ToList();
            ReturnConflict(new UnitConflict(new TaskUnit(rest), byId[failure.TaskId], failure.Files, failure.GitOutput, landedIds.ToList()), batch, ReturnStage.Land);
            returnedHere.UnionWith(rest.Select(m => m.Id));
        }

        var requeue = members.Where(t => result.NotAttempted.Contains(t.Id) && !returnedHere.Contains(t.Id)).ToList();
        if (requeue.Count > 0)
        {
            queue.InsertRange(0, TaskUnits.Build(requeue));
            events!.Write(EventTypes.Requeue, new { batch, tasks = requeue.Select(t => t.Id).ToList(), reason = "not attempted after a land failure" });
            progress.Info($"  REQUEUE {string.Join(',', requeue.Select(t => t.Id))}: not attempted after a land failure; retested next batch");
        }

        return result.Failure is null && result.NotAttempted.Count == 0;
    }

    void Verify(LandRequest request, LandResult result)
    {
        var actual = main.RevParse("refs/heads/" + epicBranch);
        if (!string.Equals(actual, result.EpicTipAfter, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' reported epic tip {result.EpicTipAfter} but '{epicBranch}' is at {actual}");
        }

        var accounted = result.Landed.Select(l => l.TaskId).Concat(result.Failure is { } f ? new[] { f.TaskId } : Array.Empty<string>()).Concat(result.NotAttempted).Order(StringComparer.Ordinal);
        if (!accounted.SequenceEqual(request.Tasks.Select(t => t.Id).Order(StringComparer.Ordinal)))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' did not account for every task of batch {request.Batch} exactly once");
        }

        if (result.Failure is null && result.NotAttempted.Count == 0
            && main.Run("rev-parse", result.EpicTipAfter + "^{tree}") != main.Run("rev-parse", request.TestedCommit + "^{tree}"))
        {
            throw new ToolException(ExitCodes.Environment, $"lander '{lander.Name}' landed a tree that differs from the tested tree ('{epicBranch}' is at {actual})", "the epic now holds untested content: inspect it before the next run");
        }
    }

    void MarkLanded(TaskSpec t, int batch, string commit)
    {
        landedIds.Add(t.Id);
        landed.Add(new LandedRecord(t.Id, batch, commit, t.Branch));
        events!.Write(EventTypes.Land, new { task = t.Id, batch, commit, branch = t.Branch });
        if (ledger!.Get(t.Id) is { Final: FinalState.Requeued })
        {
            ledger.Update(t.Id, e => e with { Final = FinalState.RebasedAndLanded });
        }
    }

    void RejectRed(TaskUnit unit, int batch)
    {
        foreach (var t in unit.Members)
        {
            progress.Info($"  REJECT {t.Id}: suite red with this task on the epic tip");
            var reason = unit.Size == 1
                ? $"suite red with this task on the epic tip (culprit isolated by bisect; later task of a pair is blamed; log {lastSuiteLog})"
                : $"suite red for stack {string.Join('+', unit.Ids)} (a stack is tested as one unit; log {lastSuiteLog})";
            ledger!.Record(Entry(t, ReturnKind.Red, ReturnStage.Suite, batch, reason) with { Final = FinalState.ReturnedRed });
        }
    }

    void ReturnConflict(UnitConflict c, int batch, string stage)
    {
        var partners = touches.Partners(c.MergedBefore.Concat(landedIds).Where(id => !c.Unit.Ids.Contains(id)), c.Files);
        var against = partners.Count > 0 ? string.Join('+', partners) : "the epic tip";
        progress.Info($"  CONFLICT {c.Offender.Id} vs {against} in {string.Join(',', c.Files)} ({stage}): returned to worker");
        events!.Write(EventTypes.Conflict, new { task = c.Offender.Id, stage, batch, files = c.Files, conflictingWith = partners });
        var single = c.Unit.Size == 1;
        foreach (var t in c.Unit.Members)
        {
            var offender = t.Id == c.Offender.Id;
            var reason = offender ? $"{stage} conflict with {against}" : $"stack member '{c.Offender.Id}' conflicted ({stage}); a stack returns as one unit";
            ledger!.Record(Entry(t, ReturnKind.Conflict, stage, batch, reason) with
            {
                ConflictingWith = partners,
                Files = offender ? c.Files : Array.Empty<string>(),
                GitOutput = offender ? c.GitOutput : "",
                Rebase = single ? RebaseState.Pending : RebaseState.Skipped,
                Final = single ? FinalState.Pending : FinalState.NeedsWorker,
            });
        }
    }

    // After each top-level batch: rebase a COPY of each conflicting task onto the epic tip; clean => requeue at the front.
    void RebaseReturned(List<TaskUnit> queue)
    {
        foreach (var entry in ledger!.Pending())
        {
            var task = original[entry.Task];
            var attempts = rebaseAttempts.GetValueOrDefault(task.Id);
            if (attempts >= config.MaxRebaseAttempts)
            {
                ledger.Record(entry with { Rebase = RebaseState.Skipped, Final = FinalState.NeedsWorker, Reason = $"{entry.Reason} (automatic rebases used: {attempts} of {config.MaxRebaseAttempts})" });
                continue;
            }

            rebaseAttempts[task.Id] = attempts + 1;
            var tip = main.RevParse("refs/heads/" + epicBranch);
            var copy = $"rebased/{config.Epic}/{task.Id}";
            var outcome = integration!.RebaseCopy(task, tip, copy);
            events!.Write(EventTypes.Rebase, new { task = task.Id, copy, clean = outcome.Clean });
            if (!outcome.Clean)
            {
                progress.Info($"  REBASE {task.Id}: conflicts again -> needs-worker");
                ledger.Record(entry with { Rebase = RebaseState.Conflict, RebaseOutput = outcome.Output, Final = FinalState.NeedsWorker });
                continue;
            }

            var files = touches.Derive(tip, "refs/heads/" + copy);
            if (files.Count == 0)
            {
                ledger.Record(entry with { Rebase = RebaseState.Clean, RebasedBranch = copy, RebaseOutput = outcome.Output, Final = FinalState.NoOpAfterRebase });
                continue;
            }

            touches.Set(task.Id, files);
            queue.Insert(0, new TaskUnit([task with { Branch = copy }]));
            progress.Info($"  REBASE {task.Id}: clean -> requeued as {copy}");
            ledger.Record(entry with { Rebase = RebaseState.Clean, RebasedBranch = copy, RebaseOutput = outcome.Output, Final = FinalState.Requeued });
        }
    }

    ReturnedEntry Entry(TaskSpec t, string kind, string stage, int batch, string reason)
    {
        var workerBranch = original[t.Id].Branch;
        return ReturnLedger.New(t.Id, workerBranch, kind, stage, batch, reason) with { RebasedBranch = t.Branch == workerBranch ? null : t.Branch };
    }

    BatchSummary Finish(Stopwatch wall, int? exitOverride, string? note)
    {
        var latest = ledger!.Latest;
        var returnedOpen = latest.Count(e => !landedIds.Contains(e.Task));
        var unprocessed = original.Keys.Where(id => !landedIds.Contains(id) && ledger.Get(id) is null).ToList();
        var exit = exitOverride ?? (landedIds.Count == original.Count ? ExitCodes.Ok : ExitCodes.Returned);
        var summary = new BatchSummary(
            SwarmJson.SchemaVersion, runId, config.Epic, epicBranch, options.Mode, lander.Name, exit, note,
            original.Count, landedIds.Count, returnedOpen,
            latest.Count(e => e.Final == FinalState.RebasedAndLanded),
            latest.Count(e => e.Final == FinalState.NeedsWorker),
            latest.Count(e => e.Final == FinalState.ReturnedRed),
            latest.Count(e => e.Final is FinalState.ReturnedBadInput or FinalState.BlockedByDependency),
            unprocessed, suiteRuns, bisectRuns, inferred, batchNo, sizeTrace,
            Math.Round(wall.Elapsed.TotalSeconds, 1), waitMs, runMs, suites, landed, batchLog, initialTouches,
            ledger.FilePath, events!.FilePath);
        SwarmJson.WriteFile(Path.Combine(runDir, RunDirectories.SummaryFileName), summary);
        events.Write(EventTypes.RunEnd, new { exitCode = exit, landed = landedIds.Count, returned = returnedOpen, note });
        progress.Info($"done: {landedIds.Count} of {original.Count} landed, {returnedOpen} returned, {suiteRuns} suite runs (exit {exit})");
        return summary;
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter "FullyQualifiedName~BatchEngineTests|FullyQualifiedName~BatchReturnsTests"`
Expected: all PASS. The exact counts in `RedTask_IsBisectedWithInferenceAndReturned_Exit1` pin the algorithm: batch [T1..T4] red (run 1); left [T1,T2] green, lands (run 2); right [T3,T4] inferred red (no run); [T3] red (run 3) -> returned; [T4] green (run 4) -> lands. Any change to bisect or inference must update this test deliberately.

- [ ] **Step 7: Run the whole suite**

Run: `cd <repo-root> && dotnet build src/Swarm.sln -warnaserror && dotnet test tests/Swarm.Tools.Tests -warnaserror && dotnet test tests/Swarm.Tests -warnaserror`
Expected: 0 warnings; all PASS (the renderer tests are unaffected).

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "Add batch engine: adaptive batches, bisect, landing, rebase-copy requeue" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 12: `batch` CLI, packaging, and `dnx` smoke test of both tools

**Files:**
- Modify: `src/Swarm.Batch.Cli/Program.cs` (replace the placeholder)
- Test: `tests/Swarm.Tools.Tests/Cli/BatchCliTests.cs`

**Interfaces:**
- Consumes: `CommonOptions`, `CliHost`, `CtrlCScope`, `ToolContext` (Task 8); `BatchEngine`, `BatchRunOptions`, `BatchModes`, `ILander`, `FastForwardLander` (Tasks 10-11); `SwarmJson` (Task 6).
- Produces: `batch run <tasks.json> [--config f] [--state d] [--slots N] [--max-wait s] [--verbosity v] [--epic E] [--run-id id] [--start N] [--min N] [--max N] [--mode batched|serial] [--experimental-no-prebatch] [--experimental-fixed N]` — stdout one `BatchSummary` line; exit = `summary.ExitCode`; for exit 4/5 after the run started, stderr also gets `error: <note>`. `public static int Swarm.Batch.Cli.Program.Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory, ILander lander)`; `Main` passes `new FastForwardLander()` (**Plan B changes only this argument**).

- [ ] **Step 1: Write the failing tests** (`tests/Swarm.Tools.Tests/Cli/BatchCliTests.cs`)

```csharp
using System.Text.Json;
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;
using BatchProgram = Swarm.Batch.Cli.Program;

namespace Swarm.Tools.Tests.Cli;

public class BatchCliTests
{
    static (int Code, string Out, string Err) Run(TempRepo repo, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = BatchProgram.Run(args, stdout, stderr, repo.Root, new FastForwardLander());
        return (code, stdout.ToString(), stderr.ToString());
    }

    static TempRepo RepoWithTask(string file = "one.txt")
    {
        var repo = TempRepo.Create();
        repo.Epic();
        repo.Branch("task/T1", "epic/E1", (file, "1\n"));
        repo.WriteTasks(new TaskLine("T1", "task/T1"));
        return repo;
    }

    static JsonElement SingleJsonLine(string stdout) =>
        JsonDocument.Parse(Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).RootElement;

    [Fact]
    public void Run_Green_PrintsSummaryAndExits0()
    {
        using var repo = RepoWithTask();
        var config = TestConfig.Write(repo, TestConfig.For(repo));
        var (code, output, _) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", config);
        Assert.Equal(0, code);
        var s = SingleJsonLine(output);
        Assert.Equal(1, s.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, s.GetProperty("tasksLanded").GetInt32());
        Assert.True(repo.HasFile("epic/E1", "one.txt"));
    }

    [Fact]
    public void Run_RedTask_Exits1AndStillPrintsSummary()
    {
        using var repo = RepoWithTask("T1.fail");
        var (code, output, _) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(ExitCodes.Returned, code);
        Assert.Equal(1, SingleJsonLine(output).GetProperty("rejectedRed").GetInt32());
    }

    [Fact]
    public void Run_MissingTasksFile_Exits3OneLine()
    {
        using var repo = RepoWithTask();
        var (code, output, err) = Run(repo, "run", "nope.json", "--config", TestConfig.Write(repo, TestConfig.For(repo)));
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Empty(output);
        Assert.StartsWith("error: tasks file '", err.TrimEnd());
        Assert.DoesNotContain('\n', err.TrimEnd());
    }

    [Fact]
    public void Run_GateTimeout_Exits5WithSummaryAndErrorLine()
    {
        using var repo = RepoWithTask();
        var config = TestConfig.For(repo);
        using var held = new SlotSemaphore(new StateLayout(repo.StateDir).SlotsDir, SlotOptions.From(config)).Acquire("other");
        var (code, output, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, config), "--max-wait", "1", "--verbosity", "quiet");
        Assert.Equal(ExitCodes.GateTimeout, code);
        Assert.Equal(5, SingleJsonLine(output).GetProperty("exitCode").GetInt32());
        Assert.StartsWith("error: no test slot free after", err.TrimEnd());
    }

    [Theory]
    [InlineData("--mode", "bogus")]
    [InlineData("--experimental-fixed", "0")]
    [InlineData("--slots", "0")]
    public void Run_BadOptions_Exit2(string option, string value)
    {
        using var repo = RepoWithTask();
        var (code, _, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)), option, value);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.StartsWith("error: ", err.TrimEnd());
    }

    [Fact]
    public void Run_EpicFlagOverridesConfig()
    {
        using var repo = RepoWithTask();
        var (code, _, err) = Run(repo, "run", Path.Combine(repo.Sandbox, "tasks.json"), "--config", TestConfig.Write(repo, TestConfig.For(repo)), "--epic", "E9");
        Assert.Equal(ExitCodes.BadInput, code);
        Assert.Contains("epic branch 'epic/E9' not found", err);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~BatchCliTests`
Expected: FAIL to compile (`Program.Run` with these parameters not defined).

- [ ] **Step 3: Implement the CLI** (`src/Swarm.Batch.Cli/Program.cs`)

```csharp
using System.CommandLine;
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Batch.Cli;

/// <summary>The <c>batch</c> tool: batched integration testing with bisect, landing green tasks on the epic branch.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory(), new FastForwardLander());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON summary line.</param>
    /// <param name="stderr">Receives progress and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <param name="lander">How green tasks land on the epic.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory, ILander lander)
    {
        var common = new CommonOptions();
        var tasks = new Argument<string>("tasks") { Description = "Tasks file: JSON array of { id, branch, dependsOn? } in queue order" };
        var epic = new Option<string?>("--epic") { Description = "Epic id (overrides config epic)" };
        var runId = new Option<string?>("--run-id") { Description = "Run id (default: <utc timestamp>-<epic>)" };
        var start = new Option<int?>("--start") { Description = "First batch size" };
        var min = new Option<int?>("--min") { Description = "Smallest batch size" };
        var max = new Option<int?>("--max") { Description = "Largest batch size" };
        var mode = new Option<string>("--mode") { Description = "batched (default) or serial (one task per suite, the baseline)", DefaultValueFactory = _ => BatchModes.Batched };
        mode.AcceptOnlyFromAmong(BatchModes.Batched, BatchModes.Serial);
        var noPrebatch = new Option<bool>("--experimental-no-prebatch") { Description = "Do not keep same-file tasks in separate batches" };
        var fixedSize = new Option<int?>("--experimental-fixed") { Description = "Fixed batch size (start = min = max)" };

        var run = new Command("run", "Merge task branches into the integration worktree, run the full suite once per batch, bisect red batches, land green tasks")
        {
            tasks, epic, runId, start, min, max, mode, noPrebatch, fixedSize,
        };
        common.AddTo(run);
        run.SetAction(p =>
        {
            if (p.GetValue(fixedSize) is < 1 or > 64)
            {
                throw new ToolException(ExitCodes.Usage, "--experimental-fixed must be between 1 and 64");
            }

            var ctx = common.Resolve(p, currentDirectory, new ConfigOverrides { Epic = p.GetValue(epic), Start = p.GetValue(start), Min = p.GetValue(min), Max = p.GetValue(max) });
            var options = new BatchRunOptions
            {
                TasksFile = Path.GetFullPath(Path.Combine(currentDirectory, p.GetValue(tasks)!)),
                Config = ctx.Config,
                RunId = p.GetValue(runId),
                Mode = p.GetValue(mode)!,
                Prebatch = p.GetValue(noPrebatch) ? false : null,
                FixedSize = p.GetValue(fixedSize),
            };
            using var ctrlC = new CtrlCScope();
            var summary = new BatchEngine(ctx.Repo, options, lander, new Progress(stderr, ctx.Verbosity)).Run(ctrlC.Token);
            stdout.WriteLine(SwarmJson.Line(summary));
            if (summary.ExitCode > ExitCodes.Returned && summary.Note is { } note)
            {
                stderr.WriteLine("error: " + note);
            }

            return summary.ExitCode;
        });

        var root = new RootCommand("batch - adaptive batched integration testing with bisect for agent swarms") { run };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd <repo-root> && dotnet test tests/Swarm.Tools.Tests -warnaserror --filter FullyQualifiedName~BatchCliTests`
Expected: all PASS.

- [ ] **Step 5: Pack both tools and smoke-test them through `dnx`** (Git Bash; PowerShell uses `dnx` instead of `dnx.cmd`)

```bash
cd <repo-root>
dotnet pack src/Swarm.TestGate.Cli -c Release -o .docs/feed -warnaserror
dotnet pack src/Swarm.Batch.Cli -c Release -o .docs/feed -warnaserror
FEED="$(pwd -W)/.docs/feed"
SMOKE=/c/Development/agent-swarm-wt/smoke-a
rm -rf "$SMOKE" "$SMOKE-wt" /c/Development/agent-swarm-wt/smoke-a-tasks.json
mkdir -p "$SMOKE" && cd "$SMOKE"
git init -q -b main
git -c user.name=s -c user.email=s@example.invalid commit -q --allow-empty -m init
git branch epic/E1
git checkout -q -b task/T1 && echo one > one.txt && git add one.txt
git -c user.name=s -c user.email=s@example.invalid commit -q -m "T1: add one" && git checkout -q main
mkdir -p .swarm && printf '{ "slots": 1, "testCommand": ["git", "--version"] }\n' > .swarm/batch.json
printf '[ { "id": "T1", "branch": "task/T1" } ]\n' > ../smoke-a-tasks.json
dnx.cmd Swarm.TestGate@0.1.0 --add-source "$FEED" -- --version
dnx.cmd Swarm.TestGate@0.1.0 --add-source "$FEED" -- run -- git --version; echo "exit $?"
dnx.cmd Swarm.TestGate@0.1.0 --add-source "$FEED" -- status; echo "exit $?"
dnx.cmd Swarm.Batch@0.1.0 --add-source "$FEED" -- run ../smoke-a-tasks.json; echo "exit $?"
git show epic/E1:one.txt
```

Expected: `0.1.0`; testgate run prints one JSON line with `"exitCode":0` and `exit 0`; status prints `{"schemaVersion":1,...,"holders":[]}` and `exit 0`; batch prints one summary line with `"tasksLanded":1` and `exit 0`; `git show` prints `one`. No prompt appears. If any re-pack is needed after a code change, bump `<Version>` in the csproj first (0.1.1, ...) and use that version in the commands. Record the observed per-call time. Then clean up: `rm -rf "$SMOKE" "$SMOKE-wt" /c/Development/agent-swarm-wt/smoke-a-tasks.json`.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "Add batch CLI and verify both tools through dnx" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

### Task 13: Documentation, decision entry, and doc sweep

**Files:**
- Create: `docs/batch-tools.md`
- Modify: `docs/decisions.md` (append one section), `docs/dnx-invocation-notes.md` (add a companion-tools section), `AGENTS.md` and `README.md` (links to `docs/batch-tools.md` and this plan)

**Interfaces:**
- Consumes: the CLI surfaces of Tasks 8 and 12, config of Task 5, JSON types of Tasks 6-7, rulings R1-R17 above.
- Produces: `docs/batch-tools.md` with anchors `#configuration` and `#tasks-file` (both referenced by error hints in code).

- [ ] **Step 1: Write `docs/batch-tools.md`** with front-matter (`created: <today>`, `updated: <today>`, `status: current`) and these sections, each stating only behaviour that the tests pin:
  1. **Requirement and NOT REAL warning**: `.NET 10+`; `Swarm.TestGate` and `Swarm.Batch` are placeholder ids, unclaimed on nuget.org, run only with `--add-source <your feed>`; link `dnx-invocation-notes.md` (no `--yes`, `dnx.cmd` in Git Bash, `--` before tool args, bump versions on re-pack).
  2. **testgate**: `run|status|reclaim --force` usage lines exactly as in Task 8 Interfaces; the result line fields of `GateResult`; slot mechanism (R8), heartbeat/expiry, reclaim rules (stale or dead local pid, never a live local holder), one-machine scope (R6), wait policy and `--max-wait` (R7).
  3. **batch**: usage line from Task 12; the flow (preflight returns, integration worktree `<worktreeRoot>/int-<epic>`, pre-batching hint, sequential merge with stop-on-conflict, one suite per batch, halving bisect with inference and later-task blame, landing through the lander, rebase-copy requeue `rebased/<epic>/<task>` with `maxRebaseAttempts`, stacks via `dependsOn`); epic branch must exist and not be checked out (R12); default fast-forward lander and that Plan B replaces it (R11).
  4. **Tasks file** (`## Tasks file`): JSON shape, `dependsOn` rules, ignored `touches`, every validation message from `TasksFileTests`.
  5. **Configuration** (`## Configuration`): the full key table with defaults and validation rules from R3, lookup order (flag, `--config`, main worktree `.swarm/batch.json`, defaults), state dir under the main worktree.
  6. **Outputs**: stdout JSON (`GateResult`, `GateStatus`, `ReclaimReport`, `BatchSummary`), `returned.jsonl` semantics (append-only, last line per task wins; kinds, stages, rebase and final values tables from `OutputTypes.cs`), `events.jsonl` types, per-suite logs, retention (R5).
  7. **Exit codes**: the table from R1, including testgate's child-exit mapping and when batch prints an `error:` line.
  8. **Windows notes**: 200-char path guard, process-tree kill and orphaned-pipe grace, crash-leftover cleanup in the integration worktree, `.cmd` resolution via PATHEXT.

- [ ] **Step 2: Append to `docs/decisions.md`** (newest last; do not edit earlier entries) and bump its `updated:` date:

```markdown
## Testgate + batch production plan (<today>)

Plan: [2026-10-03-testgate-batch.md](plans/2026-10-03-testgate-batch.md). Confirms the remaining spike-1 promotion defaults:

| Decision | Choice |
|---|---|
| Exit codes | 0 ok, 1 returned, 2 usage/config, 3 bad input, 4 environment, 5 gate wait timeout; testgate maps a failed child to 1 (child code in JSON). |
| Exit after rebase | 0 when every task landed, including rebased copies. |
| Config | `.swarm/batch.json` in the main worktree; flags win; strict keys; validated on load. |
| JSON schema | `schemaVersion: 1` everywhere; `returned.jsonl` append-only, last line per task wins. |
| Logging | stderr progress with `--verbosity`; `events.jsonl` per run; per-suite logs; keep the newest 20 finished runs. |
| Gate scope | One machine, lock files in the state dir; Windows closes the two-reclaimer race. |
| Wait policy | Polling, no FIFO; `maxWaitSec` 3600 by default, then exit 5. |
| Lander | `ILander` seam; default fast-forward to the tested commit until the squash lander (Plan B). |
```

- [ ] **Step 3: Add to `docs/dnx-invocation-notes.md`** a section `## Companion tools` with the four verified smoke commands from Task 12 Step 5 and their observed results (only what was run), and bump `updated:`.

- [ ] **Step 4: Link** `docs/batch-tools.md` and this plan from `AGENTS.md` (the "Shared docs" line) and from `README.md`.

- [ ] **Step 5: Verify the doc sweeper is clean**

Run: `cd <repo-root>/spikes/04-doc-sweeper/a && dotnet run sweep.cs -- ../../../docs --today <today>`
Expected: no error and no `index-drift` warning for `batch-tools.md`, this plan, `decisions.md` or `dnx-invocation-notes.md`. (On 2026-10-03 the sweep already reported 2 pre-existing errors, `missing-created`/`missing-updated` in `swarm-renderer-ledger.md`; they are outside this plan; report them, do not fix them here.)

- [ ] **Step 6: Commit**

```bash
git add docs AGENTS.md README.md
git commit -m "Document testgate and batch; record production rulings" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** spec section 3 delivery mechanics: sequential merge + stop-on-conflict (Task 10 `Integrate`, Task 11 returns), full suite once per batch under a CPU slot with lock-dir atomic create + heartbeat expiry (Tasks 7-8, 11), adaptive size within min/max (Task 9 `NextSize`, Task 11), halving bisect with greens landing as they pass (Task 11), squash-at-green hand-off point (Task 10 `ILander` contract; squash itself is Plan B). Spec run state: main worktree via `--git-common-dir` (Task 4), `.docs/runs` default (Task 5). Decisions "Spike 1 completion": missing branch (Task 11 `MissingBranch_...`), rebase copy max 1 + worker branch intact (Task 10 `RebaseCopy_*`, Task 11 `LandFailure_...`, `ConflictInBatch_...`), stacks via `dependsOn` as one unit (Tasks 9-11), later-task blame (Task 11 `InteractionPair_...`), pre-batching as a hint (Task 11 `Prebatch_...`). PROMOTION.md: CLI surfaces incl. `status` and `reclaim --force` (Tasks 8, 12), `testCommand` from config (Task 5), library + thin CLI (all), config file + validation (Task 5), exit codes incl. 5 (Tasks 1, 7, 11-12), JSON with `schemaVersion` and JSONL returned file (Task 6), events + verbosity + retention (Task 6, 11), gate scope / wait policy / exit-0-after-rebase (Global Constraints R1-R7). Packaging as `dnx` tools with version bump note (Tasks 1, 8, 12). Out of scope here: squash lander and per-ticket trailers (Plan B), worktree + epic tools and epic creation (Plan C), affected-test runner (stage 3 worker-local tests), hunk-range conflict prediction, multi-machine gates.
- **Placeholder scan:** every code step has complete code; the only deliberate human decisions are the real package ids/prefix and package metadata (flagged NOT REAL in constraints, csproj `Description`, Task 8 Step 7 and Task 13). `<repo-root>` and `<today>` are substitution markers, as in the reference plan.
- **Type consistency:** checked names across tasks: `ToolException.Summary/ErrorLine/Format`, `ExitCodes.*`, `GitRunner.Run/Try/Lines/RefExists/RevParse/WithIdentity/At`, `RepoPaths`, `StatePaths.Resolve/ResolveWorktreeRoot/Guard`, `StateLayout.SlotsDir/BatchLockDir/RunDir/RunsDir/GateEventsFile`, `SwarmConfig.EpicBranch`, `ConfigOverrides.None/ApplyTo`, `SlotOptions.From`, `SlotSemaphore.Acquire/TryAcquire/Status/Reclaim`, `GateRunner.Run -> GateRun(Result, Log)`, `ReturnLedger.New/Record/Update/Get/Pending/ReadLatest/FilePath`, `TaskSpec.BranchRef`, `TaskUnit.Ids/Size`, `TouchIndex.Derive/Set/Overlaps/Partners/Snapshot`, `IntegrationWorktree.Ensure/ResetTo/Integrate/RebaseCopy/Git/WorktreePath`, `UnitConflict(Unit, Offender, Files, GitOutput, MergedBefore)`, `LandRequest/LandResult/LandedTask/LandFailure/LandTask`, `BatchSummary` field order (Task 6) as constructed in `BatchEngine.Finish` (Task 11).
- **Review Focus:** each of the five lines names its tests, and each test is written out in its owning task (Tasks 1, 3, 4, 5, 6, 7, 8, 9, 10, 11).

