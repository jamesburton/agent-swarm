---
created: 2026-10-04
updated: 2026-10-05
status: current
---
# testgate and batch

Two .NET tools for agent swarms: `testgate` runs an expensive command (normally the full test suite) under a machine-wide slot limit, and `batch` merges many task branches into one integration worktree, runs the suite once per batch, bisects red batches and lands the green tasks on an epic branch. Plan: [2026-10-03-testgate-batch.md](plans/2026-10-03-testgate-batch.md). Decisions: [decisions.md](decisions.md).

Everything below was checked against the source and, where marked, by running the built tools in a scratch repository on 2026-10-04 (Windows 11, .NET SDK 10). Anything not run is labelled `unverified`.

## Requirement and package status

- Requires the .NET 10 SDK or later (`dnx` ships with it).
- **NOT REAL: nothing is published.** The package ids `Swarm.TestGate` and `Swarm.Batch` are placeholders that were not checked for ownership on nuget.org; anyone could publish a package under those ids (404 / dependency-confusion risk). Run them only from your own feed with `--add-source <your feed>`. Before any publish: reserve an owned id prefix, and add license, authors and readme metadata (`dotnet pack` currently emits warning NU5039, missing readme).
- `Swarm.TestGate` is version `0.1.2`; `Swarm.Batch` is `0.2.1` (0.2.0 made squash the default lander; 0.1.2/0.2.1 only accept the `worktree` and `epicTool` config sections of the worktree and epic tools). dnx caches an extracted version under `~/.nuget/packages/<id>/<version>` and does not pick up a re-pack of the same version: bump `<Version>` on every re-pack (see [dnx-invocation-notes.md](dnx-invocation-notes.md#gotcha-stale-tool-cache)).
- dnx rules that matter here: there is no `--yes`, use `dnx.cmd` in Git Bash, and put `--` before the tool's own arguments. Details and the smoke runs of both tools: [dnx-invocation-notes.md](dnx-invocation-notes.md).

```bash
dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- run -- dotnet test
dnx.cmd Swarm.Batch@0.2.1 --add-source FEED -- run tasks.json
```

## testgate

```text
testgate run [--cwd DIR] [--label TEXT] [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity quiet|normal|detail] [-- COMMAND...]
testgate status [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity ...]
testgate reclaim --force [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity ...]
testgate --version
```

- `run` waits for a free slot, runs the command in `--cwd` (default: current directory), releases the slot and prints exactly one JSON line (`GateResult`, see [Outputs](#outputs)) on stdout. Progress and the child's output go to stderr: testgate shows the child's output at the default `normal` verbosity and `--verbosity quiet` hides it (`batch` shows suite output on stderr only at `detail`). With no command after `--` it runs the config `testCommand` (default `dotnet test`). `--label` defaults to `testgate`. Any first command token that starts with `-` (including one that comes from `testCommand`) is rejected as usage (exit 2): put the command after `--`. A `--cwd` directory that does not exist is also exit 2.
- `status` prints one `GateStatus` line: the lock directory, the slot count and one entry per lock file (pid, host, command, heartbeat age, `stale`, `holderAlive`).
- `reclaim` without `--force` is a usage error (exit 2, `reclaim deletes lock files (pass --force to confirm)`). With `--force` it deletes stale locks and locks of dead local holders, and prints a `ReclaimReport` (`reclaimed` and `skippedLive` slot lists). It never deletes the lock of a live local holder.

How slots work:

- Slot `k` is the file `<state>/slots/slot-k.lock`, created atomically (`FileMode.CreateNew`). The first free `k` below `slots` wins. The lock records pid, host, command, acquire time and the holder's process start time (to detect pid reuse).
- The holder refreshes the file's modified time every `heartbeatSec`. A lock older than `expirySec` is stale and a waiter may take it over (the result then has `reclaimed: true`). A live local holder is never taken over even when stale (for example a suspended process); `status` reports holders on other hosts as alive (`holderAlive: true`), but both `reclaim --force` and a waiting `run` take over their locks once stale.
- On Windows an open lock file cannot be deleted by another process, which closes the race between two simultaneous reclaimers.
- Scope is one machine: the lock files live in the state directory. Two machines sharing a directory over a network share are not a supported setup (`unverified`).
- Wait policy: polling every `pollMs`, no first-in-first-out ordering, so a waiter can be overtaken. The wait ends after `maxWaitSec` (default 3600; `0` = forever) with exit 5. `--max-wait` overrides it.
- The first Ctrl+C cancels the wait or run, kills the child's process tree and releases the slot (exit 4, also when the child exits non-zero from the same signal before the cancel is seen). A second Ctrl+C terminates the process at once; a slot it held then goes stale and is reclaimed after `expirySec`. `unverified`: not exercised by a real Ctrl+C in this write-up (the cancel-versus-child-exit ordering and the second-press rule are unit-tested).
- Each finished `run` is also appended to `<state>/testgate.events.jsonl` as a `gate` event. Runs that fail to start, time out waiting or are cancelled are not logged there.

Verified by running (scratch repo): a passing command gave exit 0 and one JSON line; a failing child (`git nosuchcmd`) gave exit 1 with `exitCode: 1` in the JSON; `reclaim` without `--force` gave exit 2; `reclaim --force` and `status` on an empty state gave exit 0; a command that cannot start gave exit 4 and an `error:` line; with `--slots 1`, a second `run --max-wait 1` while the first held the slot gave exit 5 and `error: no test slot free after 1 s ...`.

## batch

```text
batch run <tasks.json> [--epic ID] [--run-id ID] [--start N] [--min N] [--max N] [--mode batched|serial]
          [--experimental-no-prebatch] [--experimental-fixed N]
          [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity quiet|normal|detail]
```

Prints exactly one JSON line (`BatchSummary`, also saved as `summary.json`) on stdout; progress goes to stderr.

Flow:

1. **Pre-run checks** (nothing is created if one fails): valid mode and sizes, the tasks file ([format](#tasks-file)), the epic branch exists and is not checked out in any worktree (batch moves it by ref), no task branch clashes with a rebase copy name (see [Tasks file](#tasks-file)), no branch named `rebased` or `rebased/<epic>` exists, and no other batch run holds the same epic (a lock under `<state>/locks/batch-<epic>`).
2. **Preflight returns.** A task whose branch does not exist is returned as `bad-input` (stage `preflight`) and the rest continue; a task that depends on a returned task is returned as `dependency`.
3. **Integration worktree** `<worktreeRoot>/int-<epic>`, detached at the epic tip. `worktreeRoot` defaults to a sibling of the main worktree named `<repo>-wt`. An existing worktree is reused and cleaned (see [Windows notes](#windows-notes)).
4. **Touch sets** are derived from git (the files each task changes against the epic tip) and used as a pre-batching hint: tasks that touch the same files are kept in separate batches. `--experimental-no-prebatch` or `"prebatch": false` turns that off.
5. **Batches.** The first batch has `start` tasks; the size then adapts within `min`..`max`. A batch merges its tasks into the integration worktree one at a time, in tasks-file order (`merge --no-ff`), and a task (or stack) that conflicts is rolled back (`merge --abort`) and returned (stage `merge`); the other tasks of the batch still merge and are tested. The full suite then runs once for the merged state, under a test slot, using config `testCommand` in the integration worktree.
6. **Green** batches are landed through the lander. **Red** batches are split in halves and each half is tested again (bisect), reusing a result by inference when the left half is green and lands cleanly and the right half then re-merges without conflicts to the very tree already seen red (the right half is then known red without a run; otherwise it is tested). A single task that is red is returned as `red`; when two tasks interact, the later task in queue order is blamed (a convention, not true attribution). `--mode serial` runs one task per suite (the baseline).
7. **Rebase-copy requeue.** A task returned for a conflict is rebased onto the epic tip as a copy branch `rebased/<epic>/<task>` (the worker's branch is never modified) and requeued, up to `maxRebaseAttempts` (default 1). A task that lands this way counts as landed and the run can still exit 0. A copy that conflicts again is `needs-worker`; a copy with nothing left to land is `no-op-after-rebase`. Tasks that are part of a stack in the tasks file are never auto-rebased, even when a lander landed part of the stack.
8. **Tool-made commits.** Integration merges and rebased copies are committed as `swarm-batch` with signing off (`commit.gpgSign=false`), so a user's signing setup cannot fail them or wait on a pinentry prompt. Integration merges skip the `pre-merge-commit` and `commit-msg` hooks (`--no-verify`). The copy rebase still runs the repository's hooks: a `pre-rebase` rejection is treated like a rebase conflict (`needs-worker`).
9. **Stacks.** Tasks linked by `dependsOn` land as one unit: they are batched together, never split by bisect, and merged in order.
10. **Landing.** The lander moves the epic branch from the tip seen at the start of the batch (compare-and-swap), either to new squashed commits whose final tree is the tested tree (`squash`, the default: one trailer-stamped commit per task, see [squash-tool.md](squash-tool.md)) or to the tested integration commit itself (`fast-forward`), so the epic only ever holds tested content. If the epic moved during the run, or git cannot update the epic ref (for example a stale lock file; the error then carries git's reason), nothing lands for that batch and the run ends with exit 4. With `squash.requireTicket`, a task without a ticket makes the squash lander land nothing from its batch; that task is returned (stage `land`) and the others are retested without it (see [squash-tool.md](squash-tool.md#guarantees)).

The epic branch must exist before the run (`git branch epic/E1 main`) and must not be checked out anywhere. Epic creation is not part of this tool.

Verified by running (scratch repo): an empty tasks file exits 0 with `note: "empty batch"`; a missing epic branch exits 3 with `epic branch 'epic/E1' not found (create it first, ...)`; a missing task branch exits 1 with one `bad-input` return and `badInput: 1` in the summary; an invalid tasks file exits 3 with an `error:` line ending in `(see docs/batch-tools.md#tasks-file)`. The merge, bisect, land and rebase paths are pinned by the `Swarm.Tools.Tests` suite and one end-to-end `dnx` run (see [dnx-invocation-notes.md](dnx-invocation-notes.md#companion-tools)).

### The ILander contract

Landing sits behind a seam so the strategy can be replaced. Signatures as in `src/Swarm.Batching/Landing.cs`:

```csharp
public sealed record LandTask(string Id, string Branch, IReadOnlyList<string> DependsOn);

public sealed record LandRequest(GitRunner Repo, GitRunner Worktree, string Epic, string EpicBranch, string EpicTipBefore, string TestedCommit, IReadOnlyList<LandTask> Tasks, int Batch, string RunId);

public sealed record LandedTask(string TaskId, string Commit);

public sealed record LandFailure(string TaskId, IReadOnlyList<string> Files, string GitOutput);

public sealed record LandResult(string EpicTipAfter, IReadOnlyList<LandedTask> Landed, LandFailure? Failure, IReadOnlyList<string> NotAttempted);

public interface ILander
{
    string Name { get; }

    LandResult Land(LandRequest request);
}
```

Rules a lander must follow (the engine checks them and exits 4 on a violation): land in request order and stop at the first failure; move the epic only from `EpicTipBefore`; never touch task branches; account for every task exactly once; report tasks as not attempted only after a failure; when nothing failed, the landed tree must equal the tested tree.

Two landers exist and `batch` uses the one named by the config key `lander`: `squash` (default; `SquashLander` in `Swarm.Squashing`, one trailer-stamped commit per task or per same-ticket run of a stack, see [squash-tool.md](squash-tool.md)) and `fast-forward` (`FastForwardLander`: moves the epic to the tested integration commit, so each task leaves one merge commit on the epic). `batch`'s `Program.Run(args, stdout, stderr, cwd, ILander)` overload passes a fixed lander; `Program.Run(args, stdout, stderr, cwd, Func<SwarmConfig, ILander>)` creates it from the resolved config, which is what `Main` does (`Landers.Create`).

## Tasks file

A JSON array of tasks in queue order. Comments and trailing commas are allowed.

```json
[
  { "id": "T1", "branch": "task/T1-parser" },
  { "id": "T2", "branch": "task/T2-cli", "dependsOn": ["T1"] }
]
```

- `id` (required): a safe name: letters, digits, `_`, `-` and single dots, starting with a letter or digit, not ending in `.lock`, at most 100 characters. Ids are unique, compared case-insensitively.
- `branch` (required): the branch to land. Rejected when empty, starting with `-`, ending in `/` or `.lock`, containing whitespace or control characters, or containing any of `..` `~` `^` `:` `?` `*` `[` `\` `@{`.
- `dependsOn` (optional): ids of tasks that appear earlier in the file. The stack lands as one unit. An unknown or later id is an error.
- `touches` and `title` are accepted and ignored (touches are derived from git). Any other key is an error.
- A branch that clashes with the rebase copy name `rebased/<epic>/<task id>` of any task in the file is rejected, because the tool owns those names. Names are compared case-insensitively (a files-backend ref store on Windows treats `Rebased/E1/T2` and `rebased/E1/T2` as the same ref), and a branch that is a `/`-prefix of a copy name (`rebased`, `rebased/E1`) or has a copy name as a prefix (`rebased/E1/T2/x`) is rejected too. An existing branch `rebased` or `rebased/<epic>` that is not in the file also blocks the run (exit 3), since no copy could be created.

Errors exit 3, are one line and end with `(see docs/batch-tools.md#tasks-file)`. Messages (prefixed with the file path):

| Message | Cause |
|---|---|
| `tasks file '<path>' not found` | no such file (no hint) |
| `invalid JSON: ...` | not valid JSON, an unknown key, or the wrong shape |
| `invalid JSON: expected an array of tasks` | the document is `null` |
| `task #N: must be an object` | an array element is `null` |
| `task #N: missing 'id'` | no `id` |
| `task id 'X' is not a safe name (...)` | bad `id` |
| `duplicate task id 'X' (ids are compared case-insensitively)` | repeated `id` |
| `task 'X': missing 'branch'` | no `branch` |
| `task 'X': branch 'B' is not a valid branch name` | bad `branch` |
| `task 'X': dependsOn 'D' must name an earlier task in the file` | unknown, later or self dependency |
| `task 'X': branch 'B' clashes with the batch tool's rebase copy 'C'` | the branch clashes with a copy name (no file-path prefix; its own hint) |
| `branch 'B' blocks the batch tool's rebase copies (...)` | an existing `rebased` or `rebased/<epic>` branch (no file-path prefix; its own hint) |

## Configuration

Both tools read one shared file. Lookup order: command-line flags win over the file; the file is `--config FILE` if given (a missing explicit file is exit 2), otherwise `<main worktree>/.swarm/batch.json` if it exists, otherwise the defaults. "Main worktree" is found through `git rev-parse --git-common-dir`, so every linked worktree shares one config and one state directory. Unknown keys, `null` values (except `worktreeRoot`) and wrong types are errors; comments and trailing commas are allowed. Every error exits 2 with one line `<source>: <message> (see docs/batch-tools.md#configuration)`; the first failing rule is reported.

```json
{
  "schemaVersion": 1,
  "slots": 2,
  "batch": { "start": 4, "min": 2, "max": 8 },
  "testCommand": ["dotnet", "test"]
}
```

| Key | Default | Rule | Flag |
|---|---|---|---|
| `schemaVersion` | `1` | must be 1 | |
| `slots` | `2` | >= 1 | `--slots` |
| `batch.start` / `batch.min` / `batch.max` | `4` / `2` / `8` | `1 <= min <= start <= max <= 64` | `--start`, `--min`, `--max` (batch only) |
| `expirySec` | `60` | >= 3 x `heartbeatSec` | |
| `heartbeatSec` | `5` | >= 1 | |
| `pollMs` | `200` | 10 to 60000 | |
| `maxWaitSec` | `3600` | >= 0; 0 = wait forever; on timeout exit 5 | `--max-wait` |
| `baseBranch` | `main` | non-empty, no whitespace, not starting with `-`; used by the squash lander to bound the epic history scanned for already-landed `Source-Commit:` trailers (`<baseBranch>..<epic>`); not used by testgate or the fast-forward lander | none |
| `lander` | `squash` | `"squash"` or `"fast-forward"` | none |
| `squash` | see [squash-tool.md#configuration](squash-tool.md#configuration) | object (`ticketPattern`, `requireTicket`, `subjectTemplate`, `author`) | none |
| `worktree` / `epicTool` | see [worktree-epic-tools.md#branch-naming](worktree-epic-tools.md#branch-naming) | objects (`branchTemplate`, `defaultKind`, `allowedPrefixes`); read by the `worktree` and `epic` tools only | none |
| `epic` | `E1` | a safe name | `--epic` (batch only) |
| `epicBranchTemplate` | `epic/{epic}` | must contain `{epic}` | |
| `testCommand` | `["dotnet","test"]` | non-empty array of non-empty strings (program, then arguments) | after `--` (testgate only) |
| `stateDir` | `.docs/runs` | non-empty; relative paths are under the main worktree; path at most 200 chars | `--state` |
| `worktreeRoot` | none (`<main parent>/<repo>-wt`) | absolute path; path at most 200 chars | |
| `maxRebaseAttempts` | `1` | 0 to 3 | |
| `prebatch` | `true` | boolean | `--experimental-no-prebatch` (batch only) |
| `keepRuns` | `20` | >= 1 | |

State directory layout (under `stateDir`): `slots/` (slot locks), `locks/batch-<epic>/` (one batch run per epic), `testgate.events.jsonl`, and `runs/<run id>/` per batch run. With the default `stateDir` the run folders are therefore at `.docs/runs/runs/<run id>/` (verified). `.docs/` is git-ignored in this repo; in another repository add the state directory to `.gitignore` yourself.

## Outputs

Every JSON document has `"schemaVersion": 1` (except nested records, which inherit the version of their parent). Property names are camelCase; stdout is one compact line.

**`testgate run` stdout, `GateResult`**: `schemaVersion`, `label`, `waitMs`, `runMs`, `slot`, `exitCode` (the child's, `-1` when killed), `reclaimed`, `killed`, `acquiredUtc`, `releasedUtc`. `killed` is set only when the process tree was killed by a timeout in the process runner; the gate itself has no run timeout, so it is `false` in practice.

**`testgate status` stdout, `GateStatus`**: `schemaVersion`, `lockDir`, `slots`, `holders[]` of `{ slot, info: { schemaVersion, pid, host, command, acquiredUtc, processStartUtc } | null, heartbeatAgeSec, stale, holderAlive }`.

**`testgate reclaim --force` stdout, `ReclaimReport`**: `schemaVersion`, `reclaimed[]`, `skippedLive[]`.

**`batch run` stdout and `<run>/summary.json`, `BatchSummary`**: `schemaVersion`, `runId`, `epic`, `epicBranch`, `mode`, `lander`, `exitCode`, `note` (why the run stopped early, or `empty batch`), `tasks`, `tasksLanded`, `returned`, `rebasedAndLanded`, `needsWorker`, `rejectedRed`, `badInput`, `unprocessed[]` (tasks neither landed nor returned because the run stopped), `fullSuiteRuns`, `bisectRuns`, `inferredRedSkipped`, `batches`, `sizeTrace[]`, `wallSeconds`, `waitMs`, `runMs`, `suites[]` (`{ gate, logFile, tasks }`), `landed[]` (`{ id, batch, commit, branch }`), `batchLog[]` (`{ batch, bisect, tasks, result }`), `derivedTouches` (task id to files), `returnedFile`, `eventsFile`. `summary.json` is written last (only the `run-end` event follows it) and marks a run as finished (unfinished runs are never pruned).

**`<run>/returned.jsonl`**: append-only, one full snapshot of a task's return record per line; **the last line per task wins**. A line that cannot be parsed (a crash left a fragment) is skipped when reading. Fields: `schemaVersion`, `utc`, `runId`, `task`, `branch` (the worker's branch, never modified), `kind`, `stage`, `batch` (0 = preflight), `conflictingWith[]` (task ids that overlap the conflicting files; empty = the epic tip), `files[]`, `reason`, `gitOutput`, `rebase`, `rebasedBranch`, `rebaseOutput`, `final`.

| Field | Values |
|---|---|
| `kind` | `conflict`, `red`, `bad-input`, `dependency` |
| `stage` | `preflight`, `merge`, `land`, `suite` |
| `rebase` | `n/a`, `pending`, `clean`, `conflict`, `skipped` |
| `final` | `pending`, `requeued`, `rebased-and-landed`, `needs-worker`, `returned-red`, `returned-bad-input`, `blocked-by-dependency`, `no-op-after-rebase` |

**`<run>/events.jsonl`**: one `{ schemaVersion, utc, runId, type, data }` line per event. Types: `run-start`, `batch-start`, `merge`, `conflict`, `suite`, `bisect`, `land`, `rebase`, `requeue`, `run-end`; testgate's own log uses `gate`. `data` is event-specific and not a stable schema (`unverified` beyond what the source writes).

**`<run>/logs/suite-NNN.log`**: the combined output of each suite run (`suiteRuns` counter, three digits).

**Retention**: before a new run starts, finished runs beyond the newest `keepRuns` (default 20) are deleted; runs without a `summary.json` are kept.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success: testgate child exited 0; batch landed every task, including tasks landed after an automatic rebase (also an empty tasks file). |
| 1 | Work came back: the testgate child exited non-zero (its code is in the JSON `exitCode`), or batch returned at least one task. |
| 2 | Usage or configuration error: unknown option or command, a first command token starting with `-`, missing `--force`, missing `--cwd` directory, invalid config, over-long path, unusable run id, bad `--experimental-fixed`. |
| 3 | Bad input: not inside a git worktree, invalid or missing tasks file, epic branch missing or checked out. |
| 4 | Environment failure: git, file system or worktree failure, a command that cannot start, another batch run holds the epic, the epic moved during the run, the epic ref could not be updated (for example a stale lock file), a lander contract violation, Ctrl+C, or an unexpected internal failure (the batch note starts `unexpected failure: <exception type>:` and `summary.json` is still written). |
| 5 | No test slot became free within `maxWaitSec`. |

When batch ends with an exit code above 1 it also prints `error: <note>` on stderr; the summary line is still printed on stdout. Pre-run failures (exit 2 and 3, and 4 for a concurrent run) print only `error: ...` and no summary. Every error is one line `error: <message> (<hint>)`. `testgate` maps any failed child to exit 1 whatever the child's own code.

## Windows notes

- **Path guard.** State, worktree-root and lock-directory paths longer than 200 characters are rejected up front (exit 2). Windows `MAX_PATH` (260) breaks git and dotnet children inside deep paths.
- **Lock files.** Slot locks are held open without delete sharing, so another process cannot delete a live holder's lock. Release retries a few times when a scanner or indexer holds the file; a lock that cannot be deleted goes stale and is reclaimed after `expirySec`.
- **Process-tree kill.** A cancelled command is killed with its whole process tree. After the main process exits, output pipes are drained for a grace period (5 s); a descendant that keeps the pipe open cannot hang the run.
- **Crash leftovers.** On reuse, the integration worktree is cleaned before the next batch: stale `index.lock`/`HEAD.lock` files are deleted, an unfinished merge or rebase is aborted, and it is force-checked-out detached at the epic tip with untracked files removed (ignored build output is kept so builds stay warm). Only this tool uses that worktree and only one batch run per epic holds it.
- **`.cmd` shims.** A bare command name is resolved on `PATH` with `PATHEXT`, so `npm` finds `npm.cmd`.
- **Output encoding.** Child output is decoded as UTF-8.

## Known limitations

- Orphans can escape the tree kill: a descendant that detaches from the process tree (or is started outside it) survives a cancel or kill. A Job Object is the likely fix and is not built.
- Heartbeat failures are swallowed silently: a live local holder whose heartbeat stopped keeps its slot until it exits; other hosts reclaim it after `expirySec`.
- A `testgate run` that fails to start, times out waiting or is cancelled leaves no entry in `testgate.events.jsonl`.
- The full `Swarm.Tools.Tests` suite takes about 13 to 15 minutes on Windows (most of it git and `dotnet` process starts). It is not hung; run a focused `--filter` while iterating.
- A few timing tests are sensitive to a busy machine; re-run a single failure alone before treating it as a regression.
- Multi-machine gates and hunk-level conflict prediction are out of scope (later plans).
