---
created: 2026-10-05
updated: 2026-10-05
status: current
---
# worktree and epic tools

Two .NET tools for agent swarms that work on one epic branch: `worktree` creates, lists and prunes per-task git worktrees branched from the epic branch, and `epic` opens, closes and reports on epics. Plan: [2026-10-03-worktree-epic.md](plans/2026-10-03-worktree-epic.md). Companion tools: [batch-tools.md](batch-tools.md), [squash-tool.md](squash-tool.md).

This page currently documents `worktree` and the shared branch naming. The `epic` half is not written yet (its tool is a later task of the plan).

Written on 2026-10-05 from the source and by running the built tool (Windows 11, git 2.54.0.windows.1, .NET SDK 10, a scratch repository, the Release build and `dnx.cmd` from a local feed). Each statement was checked by reading the named source, by a named test or by running the command; anything else is labelled `unverified`.

## Requirement and package status (NOT REAL)

- Requires the .NET 10 SDK or later (`dnx` ships with it). git 2.31 or later is the plan's floor; squash-landed work is detected only with git 2.38 or later (see [Prune rules](#prune-rules)). Only git 2.54 was run (`unverified` on older versions).
- **NOT REAL: nothing is published.** `Swarm.Worktree` (and `Swarm.Epic`) are placeholder package ids that nobody owns on nuget.org (`Swarm.Worktree` returned 404 on 2026-10-03; see [definition-format.md](definition-format.md)), so anyone could publish under them and `dnx` would download and run it (dependency confusion). Run them only with `--add-source <your feed>`. Before any publish: reserve an owned id prefix and add license, authors and readme metadata (`dotnet pack` currently prints `The package Swarm.Worktree.0.1.0 is missing a readme`; no metadata was added).
- Version as built: `Swarm.Worktree` 0.1.0. dnx caches an extracted version under `~/.nuget/packages/<id>/<version>` and does not pick up a re-pack of the same version: bump `<Version>` on every re-pack (see [dnx-invocation-notes.md](dnx-invocation-notes.md#gotcha-stale-tool-cache)). dnx rules: no `--yes`, `dnx.cmd` in Git Bash, `--` before the tool's own arguments.

```bash
dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- list
```

- **Shared config.** `worktree` reads the same `.swarm/batch.json` as testgate, batch and squash (lookup, strict keys and the error format: [batch-tools.md](batch-tools.md#configuration) and [squash-tool.md](squash-tool.md#configuration)). Every tool's loader rejects unknown keys, so a config that uses the `worktree` or `epicTool` section needs `Swarm.TestGate` 0.1.2 or later, `Swarm.Batch` 0.2.1 or later and `Swarm.Squash` 0.1.1 or later. After any config schema change re-pack every tool (with a bumped version), or the older packages reject the file.

## Branch naming

Two config sections control branch names. They share three settings:

| Key | Default | Rule |
|---|---|---|
| `branchTemplate` | `worktree`: `task/{id}-{slug}`; `epicTool`: `epic/{id}-{slug}` | must contain `{id}` and `{slug}`; `{kind}` is optional; no other placeholder |
| `defaultKind` | none | lowercase letters; required when the template uses `{kind}` |
| `allowedPrefixes` | none (any prefix) | each entry ends with `/`; matched case-sensitively, so a repository with mixed-case prefixes must list each spelling |

- `worktree` names task branches. The second section is called `epicTool` and not `epic` because the top-level `epic` key of the shared config is already the batch epic id.
- Placeholders: `{id}` (a ticket or epic id: a safe name), `{slug}` (lowercase letters and digits in hyphen-separated words, at most 40 characters, for example `login-form`) and `{kind}` (lowercase letters; `--kind` on the command line, else `defaultKind`).
- Templates are checked when the config loads (exit 2; a template is rendered once with the sample id `1`, slug `x` and the default kind to check the prefix). Every rendered branch is checked again before anything is created (exit 2) and then validated with `git check-ref-format --branch`.
- **example-org Azure DevOps.** The pipelines match only the full prefixes `feature/` and `bugfix/`; `feat/`, `fix/`, `hotfix/` and other short forms silently break pipeline triggers, so with `allowedPrefixes` they are rejected. The setup, in both sections:

```json
{"branchTemplate": "{kind}/{id}-{slug}", "defaultKind": "feature", "allowedPrefixes": ["feature/", "bugfix/"]}
```

Run record (this setup in the `worktree` section): `worktree create 11 a --base main` created `feature/11-a`; with `--kind bugfix`, `bugfix/12-b`; with `--kind fix` it exited 2 and printed `error: branch 'fix/13-c' does not start with an allowed prefix (feature/, bugfix/) (example-org pipelines trigger only on the full words feature/ and bugfix/; feat/, fix/ and other short forms silently break CI)`.

Every message, from `BranchTemplateTests` and `BranchSectionConfigTests` (all exit 2; `<s>` is `worktree` or `epicTool`; config errors are one line `<source>: <message> (see docs/batch-tools.md#configuration)`):

| Message (or the part the tests pin) | Cause |
|---|---|
| `<s>.branchTemplate must contain {id} and {slug} (got '...')` | template misses a required placeholder |
| `<s>.branchTemplate has unknown placeholder '{ticket}' (allowed: {id}, {slug}, {kind})` | other placeholder |
| `<s>.defaultKind is required when branchTemplate uses {kind}` | `{kind}` without a default |
| `<s>.defaultKind must be lowercase letters (got 'Feature')` | bad default kind |
| `<s>.allowedPrefixes entries must end with '/' (got 'feature')` | bad prefix entry |
| `<s>.branchTemplate renders 'feat/1-x', which does not start with an allowed prefix (feature/, bugfix/); example-org pipelines trigger only on the full words feature/ and bugfix/; feat/, fix/ and other short forms silently break CI` | template contradicts `allowedPrefixes` (checked at load) |
| `branch 'fix/9933-x' does not start with an allowed prefix (feature/, bugfix/)` plus the hint `example-org pipelines trigger only on the full words feature/ and bugfix/; feat/, fix/ and other short forms silently break CI` | a rendered branch (for example `--kind fix`) is not allowed; also `feat/...` and `hotfix/...` |
| `--kind given but branchTemplate '...' has no {kind}` | `--kind` with a template without `{kind}` |
| `kind '...' must be lowercase letters` | bad `--kind` |
| `slug '...' must be lowercase letters and digits in hyphen-separated words, at most 40 chars` (hint `e.g. login-form`) | bad slug (`Login`, `a--b`, `-a`, `a_b`, empty and 41 characters are all invalid) |
| `id '...' is not a safe name (...)` | for example an id with a space |
| a message naming the unknown key, for example `'branchTemplte'` | unknown key in the section |
| `invalid config ...` | `"worktree": null` |

## worktree

```text
worktree create <ticket> <slug> (--epic <id> | --base <branch>) [--kind <k>]
worktree list [--epic <id> | --base <branch>] [--all]
worktree prune [--epic <id> | --base <branch>] [--dry-run] [--force]
            each also: [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity quiet|normal|detail]
worktree --version
```

(Summary of the options in `src/Swarm.Worktree.Cli/Program.cs`, not the verbatim `--help`.) `--slots` and `--max-wait` come from the shared common options and are accepted and ignored. `--epic <id>` reads the branch from the epic record `<state>/epics/<id>.json` (written by the `epic` tool; the file layout is `EpicStore`); `--base <branch>` names a branch directly. Passing both is exit 2.

**Why a tool creates the worktrees.** The built-in `isolation: worktree` of Claude Code branches from the default branch, not from an epic branch (see [workflow.md](workflow.md), section 2), so this tool runs `git worktree add -b <task branch> <path> <fork point>` itself.

### create

- Path: `<worktreeRoot>/t-<ticket>`; `worktreeRoot` defaults to `<main parent>/<repo>-wt` ([batch-tools.md](batch-tools.md#configuration)). The path is guarded at 200 characters (exit 2, before anything is created); the integration worktree `int-<epic>` of `batch` and `squash run` lives in the same root.
- Branch: rendered from the `worktree` section (see [Branch naming](#branch-naming)); default `task/<ticket>-<slug>`. The base branch must exist (exit 3, `base branch '<b>' not found`). With `--epic`, an unknown epic is exit 3 (`epic '7' not found in <state>\epics (open it first: epic open 7 <slug>)`) and a closed epic is exit 3 (`epic '42' is closed`, hint `open a new epic for new work`; `Create_UnknownOrClosedEpic_Exit3`). `list` and `prune` accept a closed epic.
- The worktree starts at the base branch's commit at that moment (the fork point).
- **Idempotent.** Repeating the same call (same branch, same base) changes nothing and prints `"created": false` with the existing path and its current head (run, and `WorktreeManagerTests`). A branch that is already checked out for another base, a branch that already exists without a worktree, a path registered as another or as a locked worktree, or a non-empty directory at the path is an error (exit 3, or 4 for the non-empty directory), never an overwrite.
- **Branch metadata in git config.** A worktree is managed when its branch has `branch.<b>.swarm-ticket`, `branch.<b>.swarm-base` and `branch.<b>.swarm-fork-point`. They are written by `create` (retried a few times if `.git/config` is locked) and removed by the prune when the branch is deleted. Managed status is never derived from paths. A worktree made by hand, even on a `task/...` branch, is not managed and is invisible to `prune` (and to `list` without `--all`).
- **Rollback.** If recording the metadata or `git worktree add` fails, the tool undoes what it made and says what is left in the error (exit 4), for example a directory that a process holds open.
- **Windows path warning.** After creating, it measures the longest file path of the tree: when the worktree path plus the longest tracked path exceeds 259 characters, stderr gets `warning: deepest file path in the worktree will be N chars (> 259); tools without long-path support may fail there; shorten worktreeRoot` and the JSON `warnings` array carries the same text; the exit code stays 0. The 259 warning was not run end to end here; it is pinned by `WorktreeManagerTests` (the assertion on `> 259`).

stdout is one line, `WorktreeCreateResult`: `schemaVersion`, `created`, `path`, `branch`, `ticket`, `base`, `head`, `warnings`. Run record:

```text
$ worktree create 9933 login-form --base epic/42-auth      exit 0
{"schemaVersion":1,"created":true,"path":"<root>\\t-9933","branch":"task/9933-login-form","ticket":"9933","base":"epic/42-auth","head":"ac7b994...","warnings":[]}
```

### list

Prints one `WorktreeListResult` line: `schemaVersion`, `root`, `worktrees`. Each entry: `path`, `branch`, `ticket`, `base`, `baseExists`, `head`, `locked`, `lockReason`, `missing`, `dirty`, `empty`, `aheadOfBase`, `mergedVia`, `managed`, `error`, `directoryExists`. By default only managed worktrees are listed, in git's order; `--base`/`--epic` limits them to one base; `--all` also lists unmanaged worktrees on a branch (the main checkout included; ignored when `--base`/`--epic` is given). A worktree whose state cannot be read (for example a corrupt `.git` file) is still listed, with `error` set, `dirty` true and `mergedVia` null, so one bad worktree does not hide the others. `empty` means the head is still the fork point (no commits); `mergedVia` is `ledger`, `ancestor`, `content` or null (see [Prune rules](#prune-rules)); `missing` is true when the directory is gone or git calls the registration prunable, and `directoryExists` says whether the directory is still on disk.

## Prune rules

`worktree prune` considers managed worktrees only (optionally one base) and decides each from the state `list` shows. The decision table is `Pruner.Decide`, in this order, first match wins:

| # | Condition | Action | Reason in the report |
|---|---|---|---|
| 1 | state could not be assessed (`error`) | keep, even with `--force` | `could not assess: ...` |
| 2 | not managed | keep, even with `--force` | `not created by swarm` (a safety net: `prune` itself only considers managed worktrees, so the report never contains such an item) |
| 3 | locked | keep, even with `--force` | `locked (<reason>): run git worktree unlock first` |
| 4 | registration stale but the directory exists | keep, even with `--force` | `registration stale (gitdir missing) but directory exists; inspect it` |
| 5 | directory missing | `prune-metadata`; the branch is deleted when merged or with `--force` | `directory missing; branch merged (...)`, `directory missing; unmerged branch kept`, or `directory missing; unmerged branch deleted (forced)` |
| 6 | dirty (uncommitted or untracked changes) | keep; remove with `--force` | `uncommitted or untracked changes` |
| 7 | merged | remove and delete the branch | `merged (ledger\|ancestor\|content)` |
| 8 | empty (no commits yet) | keep; remove with `--force` | `no commits yet` |
| 9 | base branch gone and commits unmerged | keep; remove with `--force` | `abandoned: base '<b>' no longer exists and N commit(s) are unmerged` |
| 10 | otherwise (unmerged) | keep; remove with `--force` | `unmerged: N commit(s) not on '<base>'` |

A forced removal has the reason prefixed `forced: `. Every `remove` also deletes the task branch (`prune-metadata` deletes it only as stated in row 5). The main checkout is never removed: a task branch checked out there is reported `main checkout: never pruned` (keep).

**How "merged" is decided (`MergeCheck.LandedVia`).** A branch counts as landed when one of these holds, checked in this order, against its base (or `baseBranch` of the config when the base branch is gone):

1. `ledger`: a `batch` run recorded it landed (`summary.json` `landed[].branch`, or `returned.jsonl` with `final: rebased-and-landed`), for this epic, and only when the branch's current tip commit date is not later than the landing run's start (a branch that got a new commit after the landing is not trusted).
2. `ancestor`: `git merge-base --is-ancestor <branch> <base>`.
3. `content`: `git merge-tree --write-tree <base> <branch>` yields the base's own tree, which is how squash-landed work is found. This needs git 2.38 or later; with older git `merge-tree` fails and the branch counts as not merged, the safe direction (`unverified` on older git). It costs one `merge-tree` per worktree.

A branch whose head equals its recorded fork point is empty, not merged. `squash run` writes no run state, so a landing made with `squash run` is found only through ancestry or content; `batch` also prunes finished run folders beyond `keepRuns` (default 20), so old ledger entries disappear (see [Known limitations](#known-limitations)).

**Options.**

- `--dry-run`: changes nothing. The report shows the decided `action` per item with `done: false`; `removed` is 0 (`DryRun_ChangesNothing`, `DryRunWithForce_LeavesGitStateExactlyAsItWas`).
- `--force`: also removes dirty, unmerged, empty and abandoned worktrees, and deletes their branches (and a missing-directory item's branch). It does not touch rows 1 to 4 of the table: never a locked worktree (unlock it with `git worktree unlock` first), never a worktree whose state could not be read, never a stale registration whose directory still exists. Dirty files in a forced worktree are discarded.
- **Per worktree, not repository-wide.** Each removal is `git worktree remove [--force] <path>` for that worktree alone, which also deregisters a worktree whose directory is gone. The tool does not run `git worktree prune` in `prune` (that would drop other, unmanaged stale registrations). Note that `worktree create` does run a repository-wide `git worktree prune` when its own target path is registered with a deleted directory, and `batch` and `squash run` run one when they prepare the integration worktree: git then also drops the registration of every other unlocked worktree whose directory is missing (their branches and `swarm-*` metadata stay).
- **Branch deletion** is `git update-ref -d refs/heads/<b> <the tip that was assessed>`, so a commit made after the merge check keeps the branch (`CommitMadeAfterTheMergeCheck_KeepsTheBranch`); `branch -d` would refuse squash-landed branches and `branch -D` would delete a branch that moved. The `branch.<b>.*` config section (the metadata) is then removed. A branch that is checked out elsewhere is kept.

**Report and failures.** stdout is one `PruneReport` line: `schemaVersion`, `dryRun`, `force`, `items`, `removed`, `kept`, `failed`. An item has `path`, `branch`, `ticket`, `action` (`remove`, `prune-metadata` or `keep`), `reason`, `done` (the action was carried out; false for keep and dry runs), `branchDeleted` and `error` (a one-line failure or null). One failing item never stops the rest or loses the report of the items already processed. `removed` counts items with `done: true` (worktree removed or deregistered), `kept` counts items whose action is `keep`, `failed` counts items with an `error`.

- **An item can count in both `removed` and `failed`.** When the worktree was removed but the branch could not be deleted, the item has `done: true` and an `error` such as `branch kept: checked out at '<path>'`, `branch kept: git did not report its tip`, or `branch kept: <git's message>` (also `branch deleted but its config (branch.<b>.*) was not removed: ...`). The pinned example is a commit made after the merge check: `done: true`, `branchDeleted: false`, `error` starting `branch kept: ` (`CommitMadeAfterTheMergeCheck_KeepsTheBranch`). Exit code 4 follows `failed`, so such an item makes the exit code 4 even though its worktree is gone; the `error` text says what is left (the branch). Read `items[].error` and `done`/`branchDeleted` per item rather than inferring from the exit code.
- **Held file (C6).** On Windows a worktree removal can fail because a process holds a file there. Observed with git 2.54 (run, and pinned by `PrunerTests.HeldFile_FailsItemKeepsBranchContinues`): `git worktree remove` exits 255 with `failed to delete '<path>': Invalid argument`, but has already deleted the worktree's `.git` file and its registration, so the directory stays behind unregistered with the held file; the branch and its `swarm-*` metadata stay. The item reports `done: false` and an `error` such as `remove failed; '<path>' left behind and no longer registered (a file may be in use), delete it manually; branch kept; git: ...`. What to do: release the file, delete the directory, then delete the branch yourself (`git branch -D <branch>`; this also removes its metadata). Later `list` and `prune` runs no longer see the worktree (see [Known limitations](#known-limitations)).
- A stale registration whose directory still exists, a registered directory that git refused to remove, and an unknown registration state are all reported with the advice to inspect the directory (it may hold uncommitted work), not to delete it (`StaleRegistrationWithDirectory_KeptWithItsWorkEvenWithForce_RestProcessed`, `RemoveRefusedForARegisteredDirectory_AdvisesInspectionNotDeletion_RestProcessed`).

Run record (scratch repository, git 2.54, Windows 11): with a worktree whose branch was one commit ahead of the epic, `prune --dry-run` reported `unmerged: 1 commit(s) not on 'epic/42-auth'` (keep); `prune --force --dry-run` reported `forced: unmerged: ...` (remove) and a locked second worktree as `locked (mine): run git worktree unlock first` (keep); after the epic branch was moved to the task branch's commit, `prune` reported `merged (ancestor)`, `done: true`, `branchDeleted: true`, `"removed":1,"kept":1,"failed":0`, exit 0. With a file held open by another process, `prune --force` exited 4 with the report below on stdout and the `error:` line on stderr; the other item was removed and counted in `removed`:

```text
{"schemaVersion":1,"dryRun":false,"force":true,"items":[{... "branch":"task/7-other","action":"remove","reason":"forced: uncommitted or untracked changes","done":true,"branchDeleted":true,"error":null},{... "branch":"task/8-held","action":"remove","reason":"forced: uncommitted or untracked changes","done":false,"branchDeleted":false,"error":"remove failed; '<path>' left behind and no longer registered (a file may be in use), delete it manually; branch kept; git: error: failed to delete '<path>': Invalid argument"}],"removed":1,"kept":0,"failed":1}
error: 1 worktree(s) could not be pruned (see items[].error; a process may hold files there)
```

`git worktree list` afterwards no longer showed `t-8`, while the directory `t-8` and the branch `task/8-held` were still there.

## Exit codes

Values are those of `ExitCodes` (the same as batch, squash and testgate); source: `Program.cs` and `BaseOption`, tests in `WorktreeCliTests`.

**Output contract.** For `create` and `list`, stdout is exactly one JSON line on exit 0 and empty on exits 2 to 4. For `prune`, stdout is exactly one JSON line (the report) on exit 0 and also on exit 4 when items failed; on exits 2 and 3 it is empty. On every non-zero exit stderr carries one `error: <what> (<hint>)` line (every `ToolException` is handled by `ToolErrors.Handle`, which writes only that line); on exit 4 from `prune` that line is the last line of stderr, after any progress lines. `create` warnings go to stderr as `warning: ...` lines. Never parse stderr for data.

| Exit | Meaning |
|---|---|
| 0 | Done (`create`, `list`), or `prune` with no failed item (nothing to prune is also 0) |
| 1 | Not produced by `worktree` |
| 2 | Usage or config: parse error (unknown command or option, missing argument: one `error: ... (see --help)` line), `--epic` and `--base` together, `create` with neither, invalid slug, kind, id or branch name, branch prefix not allowed, invalid config, path over 200 characters |
| 3 | Bad input: not inside a git worktree (`OutsideRepo_Exits3`), unknown or closed epic (`create`), base branch not found, branch already exists without a worktree or is checked out for another base, locked or foreign worktree at the target path |
| 4 | Environment: git failure, a non-empty directory at the target path, `create` rolled back, or `prune` with at least one item whose `error` is set, as described under [Prune rules](#prune-rules) (the stderr line is `error: <n> worktree(s) could not be pruned (see items[].error; a process may hold files there)`, where `<n>` is `failed`) |
| 5 | Not produced by `worktree` |

Runs: `create 1 x` (neither option) exit 2, stderr `error: pass --epic <id> or --base <branch>`; `create 1 x --epic 42 --base main` exit 2, `error: pass only one of --epic or --base`; `create 2 x --epic 7` exit 3; `worktree nope` exit 2, `error: Unrecognized command or argument 'nope'. (see --help)`; stdout was empty in all of them.

## Known limitations

From the review ledger. These were not fixed in this plan.

- **Stale registration with a leftover directory stays forever.** A worktree that git calls prunable (for example its `.git` file is gone) while its directory still exists is kept with every `prune`, also with `--force`, because the directory may hold the only copy of work. It is reported `registration stale (gitdir missing) but directory exists; inspect it`. Remove or repair the directory yourself; the next `prune` then handles it as a missing-directory item.
- **A held-file failure leaves a directory and a branch that later prunes never revisit** (Ruling C2: an orphan sweep is out of scope for this plan). After the failure above git has already deregistered the worktree, so `list` and `prune` (which enumerate registered worktrees only) never see it again, and the task branch and its `swarm-*` metadata stay. `batch` and `squash run` also run a repository-wide `git worktree prune`, which deregisters worktrees whose directory was deleted by hand in the same way. Delete the directory and the branch yourself (`git branch -D`); until a later plan adds a sweep for managed branches without a worktree, such branches accumulate.
- **Locked worktrees are never removed**, not even with `--force`. Unlock them (`git worktree unlock <path>`) first.
- **Unmanaged worktrees are never touched.** A worktree without the branch metadata (made by hand, or the `int-<epic>` integration worktree of `batch`) is invisible to `prune`.
- **`RunDirectories.Prune` keeps the newest 20 finished runs across all epics** (Ruling C3; a limitation of the `batch` run-state code, not of `worktree`). A busy epic can delete another epic's run folders and with them the evidence behind the `tasks-returned` close blocker and the `ledger` merge check. The fix is to prune per epic; it is not done.
- **The exit-code mapping counts kept branches as failures.** A forced or merged removal whose branch could not be deleted is `done: true` with an `error`, counted in both `removed` and `failed`, and exits 4 (see [Prune rules](#prune-rules)).
- **`--force` discards uncommitted files** of any managed, unlocked worktree whose directory is present, including one whose branch is checked out outside the managed root (a design question left open in the review; from the code, not run).
- **Not run.** git older than 2.38 (the content check), non-Windows hosts, and the 259-character path warning end to end (a unit test pins it).
