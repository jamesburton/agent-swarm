---
created: 2026-10-05
updated: 2026-10-06
status: current
---
# worktree and epic tools

Two .NET tools for agent swarms that work on one epic branch: `worktree` creates, lists and prunes per-task git worktrees branched from the epic branch, and `epic` opens, closes and reports on epics. Plan: [2026-10-03-worktree-epic.md](plans/2026-10-03-worktree-epic.md). Companion tools: [batch-tools.md](batch-tools.md), [squash-tool.md](squash-tool.md).

Sections: [Branch naming](#branch-naming), [worktree](#worktree), [Prune rules](#prune-rules), [epic](#epic), [Close blockers](#close-blockers), [Status for orchestrators](#status-for-orchestrators), [Exit codes](#exit-codes), [Windows notes](#windows-notes), [Known limitations](#known-limitations).

Written on 2026-10-05 from the source and by running the built tools (Windows 11, git 2.54.0.windows.1, .NET SDK 10.0.401, scratch repositories, the Release build and `dnx.cmd` from a local feed; the full `dnx` run of all five tools is in [dnx-invocation-notes.md](dnx-invocation-notes.md#worktree-and-epic-010)). Each statement was checked by reading the named source, by a named test or by running the command; anything else is labelled `unverified`.

## Requirement and package status (NOT REAL)

- Requires the .NET 10 SDK or later (`dnx` ships with it). git 2.31 or later is the plan's floor; squash-landed work is detected only with git 2.38 or later (see [Prune rules](#prune-rules)). Only git 2.54 was run (`unverified` on older versions).
- **Not published yet.** The package ids are `AgentSwarm.Worktree` and `AgentSwarm.Epic` (renamed on 2026-10-06 from the placeholders `Swarm.Worktree` and `Swarm.Epic`); nuget.org returned 404 for all six `AgentSwarm.*` ids on 2026-10-06, so until the first publish anyone could publish under them and `dnx` would download and run it (dependency confusion). Run them only with `--add-source <your feed>` until a release has been published (see [publishing.md](publishing.md)). Package metadata (MIT license, authors, repository, readme) comes from `src/Swarm.Packaging.props`; `dotnet pack` of the solution printed no warnings on 2026-10-06. Run records on this page and in [dnx-invocation-notes.md](dnx-invocation-notes.md) were made before the rename and show the old `Swarm.*` ids.
- Versions as built: `AgentSwarm.Worktree` 0.1.0 and `AgentSwarm.Epic` 0.1.0. dnx caches an extracted version under `~/.nuget/packages/<id>/<version>` and does not pick up a re-pack of the same version: bump `<Version>` on every re-pack (see [dnx-invocation-notes.md](dnx-invocation-notes.md#gotcha-stale-tool-cache)). dnx rules: no `--yes`, `dnx.cmd` in Git Bash, `--` before the tool's own arguments.

```bash
dnx.cmd AgentSwarm.Worktree@0.1.0 --add-source FEED -- list
dnx.cmd AgentSwarm.Epic@0.1.0 --add-source FEED -- status
```

- **Shared config.** `worktree` and `epic` read the same `.swarm/batch.json` as testgate, batch and squash (lookup, strict keys and the error format: [batch-tools.md](batch-tools.md#configuration) and [squash-tool.md](squash-tool.md#configuration)). Every tool's loader rejects unknown keys, so a config that uses the `worktree` or `epicTool` section needs `Swarm.TestGate` 0.1.2 or later, `Swarm.Batch` 0.2.1 or later and `Swarm.Squash` 0.1.1 or later. After any config schema change re-pack every tool (with a bumped version), or the older packages reject the file.

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
- **Idempotent.** Repeating the same call (same branch, same base) changes nothing and prints `"created": false` with the existing path and its current head (run, and `WorktreeManagerTests`). A branch that is already checked out for another base, a branch that already exists without a worktree, a path registered as another or as a locked worktree, or a non-empty directory at the path is an error (exit 3, or 4 for the non-empty directory), never an overwrite. A repeat whose existing worktree is a stale registration (its directory deleted, or its `.git` file gone) is exit 3, `the worktree of branch '<b>' at '<path>' is a stale registration (...)`, with a hint to recreate it (`git worktree remove`, then `git worktree add <path> <branch>`) or to repair it, instead of a `"created": false` that points at nothing (`Create_RepeatForAWorktreeWhoseDirectoryIsGone_IsBadInputWithAHint`).
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
- **Per worktree, not repository-wide.** Each removal is `git worktree remove [--force] <path>` for that worktree alone, which also deregisters a worktree whose directory is gone. No tool runs the repository-wide `git worktree prune` (it would also drop other stale registrations, including one whose `.git` file is gone but whose directory still holds work). `worktree create`, when its own target path is a stale registration, removes that registration only (`git worktree remove <path>`), and only after checking that the directory is missing or empty (`Create_StaleRegistrationAtItsPath_DeregistersOnlyThatPath`, `Create_StaleRegistrationWithFilesAtItsPath_IsRefusedBeforeDeregistering`); `batch` and `squash run` do the same for their own integration worktree (`IntegrationWorktreeTests.Ensure_DeletedIntegrationDirectory_IsRecreatedAndOtherStaleRegistrationsAreLeftAlone`).
- **Branch deletion** is `git update-ref -d refs/heads/<b> <the tip that was assessed>`, so a commit made after the merge check keeps the branch (`CommitMadeAfterTheMergeCheck_KeepsTheBranch`); `branch -d` would refuse squash-landed branches and `branch -D` would delete a branch that moved. The `branch.<b>.*` config section (the metadata) is then removed. A branch that is checked out elsewhere is kept.

**Report and failures.** stdout is one `PruneReport` line: `schemaVersion`, `dryRun`, `force`, `items`, `removed`, `kept`, `failed`. An item has `path`, `branch`, `ticket`, `action` (`remove`, `prune-metadata` or `keep`), `reason`, `done` (the action was carried out; false for keep and dry runs), `branchDeleted` and `error` (a one-line failure or null). One failing item never stops the rest or loses the report of the items already processed. `removed` counts items with `done: true` (worktree removed or deregistered), `kept` counts items whose action is `keep`, `failed` counts items with an `error`.

- **An item can count in both `removed` and `failed`.** When the worktree was removed but the branch could not be deleted, the item has `done: true` and an `error` such as `branch kept: checked out at '<path>'`, `branch kept: git did not report its tip`, or `branch kept: <git's message>` (also `branch deleted but its config (branch.<b>.*) was not removed: ...`, and `branch delete failed: <reason>` when the branch step itself failed, for example a git process that could not start: `NonGitExceptionInTheBranchStep_KeepsTheItemDone`). The pinned example is a commit made after the merge check: `done: true`, `branchDeleted: false`, `error` starting `branch kept: ` (`CommitMadeAfterTheMergeCheck_KeepsTheBranch`). Exit code 4 follows `failed`, so such an item makes the exit code 4 even though its worktree is gone; the `error` text says what is left (the branch). Read `items[].error` and `done`/`branchDeleted` per item rather than inferring from the exit code.
- **Held file (C6).** On Windows a worktree removal can fail because a process holds a file there. Observed with git 2.54 (run, and pinned by `PrunerTests.HeldFile_FailsItemKeepsBranchContinues`): `git worktree remove` exits 255 with `failed to delete '<path>': Invalid argument`, but has already deleted the worktree's `.git` file and its registration, so the directory stays behind unregistered with the held file; the branch and its `swarm-*` metadata stay. The item reports `done: false` and an `error` such as `remove failed; '<path>' left behind and no longer registered (a file may be in use), delete it manually; branch kept; git: ...`. What to do: release the file, delete the directory, then delete the branch yourself (`git branch -D <branch>`; this also removes its metadata). Later `list` and `prune` runs no longer see the worktree (see [Known limitations](#known-limitations)).
- A stale registration whose directory still exists, a registered directory that git refused to remove, and an unknown registration state are all reported with the advice to inspect the directory (it may hold uncommitted work), not to delete it (`StaleRegistrationWithDirectory_KeptWithItsWorkEvenWithForce_RestProcessed`, `RemoveRefusedForARegisteredDirectory_AdvisesInspectionNotDeletion_RestProcessed`).

Run record (scratch repository, git 2.54, Windows 11): with a worktree whose branch was one commit ahead of the epic, `prune --dry-run` reported `unmerged: 1 commit(s) not on 'epic/42-auth'` (keep); `prune --force --dry-run` reported `forced: unmerged: ...` (remove) and a locked second worktree as `locked (mine): run git worktree unlock first` (keep); after the epic branch was moved to the task branch's commit, `prune` reported `merged (ancestor)`, `done: true`, `branchDeleted: true`, `"removed":1,"kept":1,"failed":0`, exit 0. With a file held open by another process, `prune --force` exited 4 with the report below on stdout and the `error:` line on stderr; the other item was removed and counted in `removed`:

```text
{"schemaVersion":1,"dryRun":false,"force":true,"items":[{... "branch":"task/7-other","action":"remove","reason":"forced: uncommitted or untracked changes","done":true,"branchDeleted":true,"error":null},{... "branch":"task/8-held","action":"remove","reason":"forced: uncommitted or untracked changes","done":false,"branchDeleted":false,"error":"remove failed; '<path>' left behind and no longer registered (a file may be in use), delete it manually; branch kept; git: error: failed to delete '<path>': Invalid argument"}],"removed":1,"kept":0,"failed":1}
error: 1 prune item(s) failed (see items[].error in the report on stdout)
```

`git worktree list` afterwards no longer showed `t-8`, while the directory `t-8` and the branch `task/8-held` were still there.

## epic

```text
epic open <id> <slug> [--from <branch>] [--kind <k>]
epic status [<id>] [--into <branch>]
epic close <id> [--into <branch>] [--force] [--dry-run] [--delete-branch]
            each also: [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity quiet|normal|detail]
epic --version
```

(Summary of the options in `src/Swarm.Epic.Cli/Program.cs`, not the verbatim `--help`.) `--slots` and `--max-wait` are the shared common options, accepted and ignored. Every command prints exactly one JSON line with `schemaVersion: 1` on success (see [Exit codes](#exit-codes) for `close`).

**Branch model** ([workflow.md](workflow.md), sections 2 and 4). An epic branch is cut from the active branch (config `baseBranch`, or `--from`). Tasks work in worktrees branched from it ([worktree](#worktree)); `batch` and `squash run` land each ticket on the epic branch as one squashed, trailer-stamped commit ([squash-tool.md](squash-tool.md#what-lands)). `epic close` lands the epic on the active branch with one `--no-ff` merge commit and never squashes it, so `git log --first-parent <active branch>` reads one line per epic and the ticket commits stay reachable through the merge's second parent (`EpicCloserTests` assert the parent count; `EpicCliTests.Close_Merged_Exit0` asserts three entries in `rev-list --parents -n 1`).

### open

- Renders the branch from the `epicTool` section ([Branch naming](#branch-naming); default `epic/<id>-<slug>`), checks it with `git check-ref-format --branch` (exit 2), then creates it at the base branch's current commit **without checking it out**: `batch` and `squash run` refuse an epic branch that is checked out (exit 3), so it must stay free.
- Writes the record `<state>/epics/<id>.json` (`EpicRecord`: `schemaVersion`, `id`, `slug`, `branch`, `baseBranch`, `baseCommit`, `createdUtc`, `state` (`open` or `closed`), `closedUtc`, `mergeCommit`, `mergedInto`). `<state>` is the shared run-state directory (config `stateDir`, default `.docs/runs` in the main worktree). `worktree create --epic <id>` reads the branch from this record.
- **Idempotent.** The same id and slug on an existing open epic whose branch exists prints `"created": false` and the stored record (`EpicOpenerTests.Open_IsIdempotent`); `--from` is not re-checked then. The same id with another branch, or a closed epic with that id, is exit 3 (`epic '<id>' already exists as '<branch>' (<state>)`, hint `use another id`); an existing branch without a record is exit 3 (`branch '<b>' already exists`); a missing base is exit 3 (`base branch '<b>' not found`). A disallowed `--kind` is exit 2 with the example-org hint and creates neither branch nor record (`EpicOpenerTests.FixKind_CreatesNothing`, `EpicCliTests.Open_DisallowedKind_Exit2NoStdout`); for the other failures "nothing created" is read from the code (every check runs before `git branch`) and not asserted by a test. If the record cannot be saved after the branch was created, the branch is deleted again (guarded `update-ref -d <branch> <base commit>`), so a retry is not stuck on `branch already exists`: exit 4, `could not save the epic record '<path>': <reason> (branch '<b>' removed again)` (`RecordCannotBeSaved_BranchIsRemovedAgainSoARetrySucceeds`).
- stdout, `EpicOpenResult`: `schemaVersion`, `created`, `id`, `slug`, `branch`, `baseBranch`, `baseCommit`, `batchEpic`, `warnings`. Each warning also goes to stderr as a `warning: ...` line, never to stdout (`EpicCliTests.Open_WarningsGoToStderrNotStdout`).

**`batchEpic` and choosing `epicBranchTemplate`.** `batch run --epic <x>` and `squash run --epic <x>` address the branch `epicBranchTemplate` with `{epic}` replaced by `x` (default `epic/{epic}`). `batchEpic` is the `x` that maps back to this epic's branch (`EpicNaming.BatchEpicId`), so with the defaults `epic/42-auth` has batch epic id `42-auth` (run: `open 42 auth` printed `"batchEpic":"42-auth"`). When the template cannot express the branch, `batchEpic` is null and the warning is `batch cannot address '<branch>' with epicBranchTemplate '<template>'; change epicBranchTemplate (e.g. to the epic branch prefix + {epic})` (`EpicOpenerTests.UnaddressableByBatch_WarnsWithNullBatchEpic`). Choose `epicBranchTemplate` as the epic branch prefix plus `{epic}`: for example-org, with `epicTool.branchTemplate` `{kind}/{id}-{slug}`, use `feature/{epic}` so that batch addresses `feature/9933-login` as `--epic 9933-login` (`EpicOpenerTests.BatchEpicId_InvertsTheBatchTemplate`). Bugfix epics then need a second config (or `epicBranchTemplate` `bugfix/{epic}`), since one template has one prefix.

### status

`epic status` prints one `EpicStatusList` line: `schemaVersion`, `epics` and `unreadable`, one [`EpicStatus`](#status-for-orchestrators) per epic, every epic in the state directory ordered by id, or just `<id>`. An unknown id is exit 3 (`epic '<id>' not found in <state>\epics`, hint `open it first: epic open <id> <slug>`; `EpicCliTests.Status_AllAndOne`). `--into` names the branch to assess against (default: the epic's `baseBranch`); a missing one is exit 3 (`branch '<b>' not found`). With no epics the list is empty and the exit is 0 (from the code: `EpicStore.All` returns nothing for a missing directory; not run). Without `<id>`, a file in `<state>/epics` that cannot be read as a record (an unsafe file name such as `notes copy.json`, invalid JSON, an I/O error) never fails the listing: it is skipped with a `warning: epic file '<path>' skipped: <reason>` line on stderr and listed in `unreadable` (`path`, `error`), and the other epics are reported (`EpicStoreTests.All_StrayFiles_AreReportedPerFileAndDoNotHideTheOthers`, `EpicCliTests.Status_StrayRecordFile_IsAWarningAndAnUnreadableEntry_NotAFailure`). `unreadable` is always present (empty for `status <id>`, where an unreadable record is the exit-3 error as before).

### close

`epic close <id>` re-checks the [close blockers](#close-blockers) and, when none remain, merges the epic `--no-ff` into `--into` (default: the epic's `baseBranch`), records the epic as closed (`state`, `closedUtc`, `mergeCommit`, `mergedInto`) and prints one `EpicCloseResult` line:

| Field | Meaning |
|---|---|
| `schemaVersion` | 1 |
| `result` | `merged` (exit 0), `dry-run` (exit 0), `blocked` (exit 1) or `conflict` (exit 1) |
| `id`, `branch`, `into` | the epic, its branch and the target branch |
| `mergeCommit` | the merge commit (`merged` only, else null) |
| `tickets` | tickets from the `Ticket:` trailers of the commits being merged, in first-seen order |
| `blockers` | blockers that stopped the close (`blocked` only) |
| `waived` | blockers waived by `--force` |
| `conflictFiles` | conflicting files (`conflict` only) |
| `message` | the merge message ([format](#the-merge-message)) |
| `branchDeleted` | true when `--delete-branch` deleted the epic branch |
| `warnings` | one-line warnings that did not stop the close |

- **Warnings go to stderr for every result.** Each `warnings` entry is printed as `warning: <w>` on stderr after the JSON line and before any `error:` line, whatever the result (`EpicCliTests.Close_DeleteBranchCheckedOut_KeepsBranchAndPrintsWarning` pins the `merged` case; in this build only `merged` and `conflict` results can carry warnings; the order (warnings, then the `error:` line) and the `conflict` and `blocked` cases are pinned on the CLI's printer with constructed results: `EpicCliTests.ReportClose_Conflict_PrintsWarningsThenTheErrorLine`, `ReportClose_Blocked_PrintsWarningsBeforeTheErrorLine`, `ReportClose_Merged_PrintsWarningsAndNoError`; a real close whose conflict carried a warning was not run). Warnings are: the temporary close worktree could not be removed (also after a conflict), the epic branch moved during the close (only its commits up to the merged tip are merged; the branch is kept), or `--delete-branch` kept the branch.
- **`--dry-run`** runs every check and builds the message, changes nothing and prints `result: dry-run` (`EpicCliTests.Close_DryRun_Exit0_NothingChanged`).
- **`--force`** waives only the waivable blockers (`tasks-returned`, `run-unfinished`, `worktrees-unmerged`); they are listed in `waived`. Every other blocker still blocks (`EpicCloserTests.Force_DoesNotWaiveActiveDirty`, `Force_DoesNotWaiveNothingToMerge`, `BatchRunning_BlocksEvenWithForce`).
- **`--delete-branch`** deletes the epic branch after the merge with a guarded `git update-ref -d refs/heads/<epic> <merged tip>`, then removes its `branch.<epic>.*` config section. It is not `git branch -d` (which would refuse a branch not merged into the current HEAD). The branch is kept, with a warning, when it moved after the merge (`DeleteBranch_KeepsEpicThatMovedDuringClose`) or when it is checked out in any worktree (`'<epic>' was kept: it is checked out at '<path>'`; `DeleteBranch_KeepsEpicCheckedOutInAWorktree`, and through the CLI `EpicCliTests.Close_DeleteBranchCheckedOut_KeepsBranchAndPrintsWarning`), or when a worktree is rebasing it, which leaves that worktree detached (`'<epic>' was kept: it is being rebased in '<path>'`; `DeleteBranch_KeepsEpicBeingRebasedInAWorktree`). Run: `close 42 --delete-branch` printed `"branchDeleted":true` and `git branch --list` showed only `main`.
- A second close of a closed epic is exit 3 with no stdout: `error: epic '42' is already closed (merged into 'main' at <sha>)` (run).

Run record, verbatim from the [dnx run](dnx-invocation-notes.md#worktree-and-epic-010) (rows C33 and C34; one ticket landed by `squash run`, one by `batch`; the paths are those of the scratch run):

```text
$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --delete-branch
[stdout]
{"schemaVersion":1,"result":"merged","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":"51d535858eb130b5441a66f80499086f86e273a0","tickets":["9933","9934"],"blockers":[],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":true,"warnings":[]}
[stderr]
[exit 0, 18.3 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42
[stdout]
[stderr]
error: epic '42' is already closed (merged into 'main' at 51d535858eb130b5441a66f80499086f86e273a0)
[exit 3, 5.1 s]
```

A blocked close (exit 1) prints the JSON line with `result: blocked` and then one stderr line, `error: epic '<id>' not closed: <first blocker's detail> (<n> blocker(s); see blockers)` (`EpicCliTests.Close_Blocked_Exit1WithJsonAndOneErrorLine`). A conflict (exit 1) prints the JSON line with `result: conflict` and `error: merging '<branch>' into '<into>' conflicts in <files> (merge '<into>' into the epic and resolve, then close again)` (`EpicCliTests.Close_Conflict_Exit1`); nothing moves (`EpicCloserTests.Conflict_ReportsFilesAndLeavesMainAlone`; in the run, `main` stayed at its own last commit and no `close-43` worktree was left). Verbatim from the same run (rows C31 and C44): a tracked edit in `main` blocks even with `--force` (`active-dirty` is not waivable), and a second epic that changes the same line of `shared.txt` as `main` conflicts:

```text
$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --force
[stdout]
{"schemaVersion":1,"result":"blocked","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[{"code":"active-dirty","detail":"\u0027main\u0027 is checked out at \u0027<scratch>\\smoke\u0027 with uncommitted changes to tracked files","waivable":false}],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
error: epic '42' not closed: 'main' is checked out at '<scratch>\smoke' with uncommitted changes to tracked files (1 blocker(s); see blockers)
[exit 1, 12.5 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 43
[stdout]
{"schemaVersion":1,"result":"conflict","id":"43","branch":"epic/43-conflict","into":"main","mergeCommit":null,"tickets":["9935"],"blockers":[],"waived":[],"conflictFiles":["shared.txt"],"message":"Merge epic 43-conflict (epic/43-conflict) into main\n\nEpic: 43 (batch epic 43-conflict)\nTickets: 9935\nRuns: 1\n\n- 9935 (manual): Change shared","branchDeleted":false,"warnings":[]}
[stderr]
error: merging 'epic/43-conflict' into 'main' conflicts in shared.txt (merge 'main' into the epic and resolve, then close again)
[exit 1, 18.0 s]
```

On Windows, restoring such an edit with `git checkout -- <file>` can leave `active-dirty` firing on a worktree that `git status` calls clean (a line-ending bug; see [Known limitations](#known-limitations)).

## Close blockers

`epic status` and `epic close` compute blockers with one function, `EpicAssessor.Assess`, so `status` reports exactly what `close` would enforce at that moment. `readyToClose` is true when there are none.

| Code | Meaning | Waivable with `--force` |
|---|---|---|
| `batch-running` | a live holder of the per-epic lock `<state>/locks/batch-<batch epic id>`, which `batch run` and `squash run` share; detail `a batch or squash run holds epic '<id>'; wait for it to finish` | no |
| `run-unfinished` | a batch run of this epic has no `summary.json` (crashed or killed); not raised while the lock is held | yes |
| `tasks-returned` | tasks returned or left unprocessed by their latest run that have not landed since; detail `<n> task(s) not landed: <task> (<final or state>), ...` | yes |
| `worktrees-unmerged` | managed task worktrees on the epic with unmerged commits, uncommitted edits (even when the branch reads as merged), a state that could not be assessed, or a stale registration whose directory exists | yes |
| `nothing-to-merge` | the epic has no commits that are not on the target | no |
| `active-dirty` | the target branch is checked out in a worktree with changes to **tracked** files (untracked files do not count: `UntrackedOnly_DoesNotBlock`) | no |
| `active-behind-upstream` | the target is behind its upstream **as last fetched** (`'<into>' is <n> commit(s) behind '<upstream>' as last fetched; pull first`) | no |
| `epic-closed` | the epic is already closed | no |
| `branch-missing` | the epic branch does not exist | no |

Rules the tests pin (`EpicAssessorTests`, `EpicCloserTests`):

- **The lock rule is the acquire rule.** `batch-running` uses `SlotSemaphore.BlocksAcquire`, the same rule `batch` and `squash run` use to take the lock: a lock with a fresh heartbeat always counts, even when its holder looks dead (`LockHolder_CountsAsRunningOnlyWhenBatchCouldNotTakeTheLock`, `FreshLockWhoseHolderCannotBeConfirmed_BlocksAsRunning`). So a crashed **local** run keeps blocking (not waivable) until its lock is older than `expirySec`.
- **`epic close` takes the per-epic lock itself** (same construction as `batch` and `squash run`: one slot, no wait) around the merge, then assesses again under it, so a run cannot move the epic during the merge (`EpicLock_HeldDuringMergeAndReleasedAfter`, `BlockerAppearingBeforeLock_IsCaughtByReassessment`). A lock held when the close starts is the `batch-running` blocker (exit 1); a lock taken between the first assessment and the close's own acquire is exit 4 (`a batch or squash run holds epic '<id>'; nothing merged`; `LockTakenAfterAssessment_FailsWithEnvironmentAndChangesNothing`). An epic without a batch epic id has no lock, because neither tool can reach its branch.
- **Returned tasks clear when they land later.** A task returned by one run and landed by a later run no longer blocks (`ReturnedTask_BlocksUntilALaterRunLandsIt`). `squash run` writes no run state, so a returned task landed later by `squash run` is cleared through the merged check of [Prune rules](#prune-rules) (ancestry or content on the epic): `ReturnedTask_LandedLaterBySquashRun_DoesNotBlock` runs a real `SquashRunner`. An unprocessed task with no branch recorded cannot be checked that way and keeps blocking (waivable).
- **Runs of other epics are ignored** (`RunsOfOtherEpics_AreIgnored`), and an empty or partly pruned run folder does not crash the assessment (`EmptyOrPartlyPrunedRunFolder_DoesNotCrash`).

**Close mechanics.**

1. The merge is made in a tool-owned detached worktree `<worktreeRoot>/close-<id>` (a previous one left behind is replaced: `StaleCloseWorktree_IsReplaced`) at the commit of the target read under the lock: `git merge --no-ff --no-edit --no-verify --no-log -m <message> <epic tip>`, with the user's git identity. The temporary worktree is removed afterwards; if a file there is held open, the result carries a warning naming what is left (`HeldFileInCloseWorktree_ReportedNotClaimedRemoved`).
2. **Hooks (review ruling C6).** The tool-made merge commit uses `--no-verify`, so the repository's `pre-merge-commit` and `commit-msg` hooks (for example commitlint) never see it (`RejectingHooks_DoNotRunOnTheToolMadeMerge`), and `--no-log`, so `merge.log` never appends a shortlog and the message is exactly the one below (`MergeLogConfig_DoesNotChangeTheToolsMessage`). This matches `batch`'s integration merges, which also use `--no-verify` (`IntegrationWorktree`). The alternative, not chosen: keep the hooks and let a hook rejection fail the close with exit 4. A team that enforces commit-message rules on the active branch therefore gets epic merge commits that bypass them.
3. "Already up to date" (the target already contains the epic) is never recorded. Normally the re-assessment under the lock catches it first as `nothing-to-merge` (exit 1, `TargetComesToContainTheEpicBeforeTheLock_IsBlockedNotRecorded`); the merge-time guard behind it (exit 4, `'<into>' already contains '<epic>'`) is defensive and has no test of its own. The message is passed to git with `-F <file>` (a file in the temporary worktree's git dir), not `-m`, so it is not limited by the Windows command line (`MergeMessageLongerThanTheWindowsCommandLine_IsPassedByFile`).
4. **Moving the target.** If the target is checked out in a worktree (normally the main one), that worktree is fast-forwarded with `git merge --ff-only --no-overwrite-ignore`, so its files follow (`ActiveCheckedOut_FastForwardsWorkingTree`); an untracked file in the way, or an **ignored** local file the epic adds (for example `.env`), stops it with exit 4 and nothing merged (`UntrackedFileInTheWay_FailsFastForwardAndChangesNothing`, `IgnoredLocalFileTheEpicAdds_IsNotOverwritten`). The fast-forward, the checks just before it and the files it writes use the repository's own line-ending settings (`core.autocrlf`), as the user's git does (`CleanCrlfCheckoutUnderRepoAutoCrlf_FastForwardsAndWritesTheUsersLineEndings`). **Caveat: a fast-forward can fail half-way.** On Windows a file held open by another process cannot be replaced; git then stops with `unable to unlink old '<file>'` and leaves the branch and index at the old commit, but has already rewritten other files. The target did not move and the record stays open, but the checkout is partly the merge's. The exit-4 error then lists what to undo: `restore them with: git -C <path> checkout -- <files>` and, for files the merge added, `delete the file(s) it created: <files>` (`HeldFileStopsTheFastForwardHalfWay_ReportsHowToRestoreTheTree`, which follows the advice and then closes). Only files the merge touches that now hold the merge's content are listed, so an edit of your own made during the close is never offered for `checkout --` (`UserEditMadeAfterTheAssessment_StopsTheFastForwardAndIsNeverOfferedForCheckout`). Release the file, run those commands, then close again. Otherwise the ref moves with a compare-and-swap `git update-ref` from the commit the merge was built on (`ActiveNotCheckedOut_UpdatesRefOnly`). A target that moved meanwhile is exit 4, `'<into>' moved during close; nothing merged` (`TargetMovedDuringClose_*`); a ref that cannot be locked reports git's reason with a stale-lock hint naming `refs/heads/<branch>.lock` or, in reftable repositories (the default of `git init` with git 2.54 here), `reftable/tables.list.lock` (`StaleRefLock_ReportsGitsReasonNotMoved`).
5. **Rebase guard.** If any worktree is rebasing the target branch (merge or apply backend), close is exit 4 with `'<into>' is being rebased in '<path>'; nothing merged`, because `git rebase --abort` would reset the branch and drop the merge (`ActiveBranchBeingRebased_RefusesAndChangesNothing`).
6. After the move the target is verified to point at the merge commit, and only then is the record saved as closed (`TargetNotAtTheMergeCommitAfterTheMove_IsNotRecordedClosed`). Every failure before the move leaves the target, the record and the epic branch untouched (the working tree of a half-done fast-forward is the exception above).
7. **No fetch, no push.** The tool never contacts a remote. "Behind upstream" compares with the last fetched upstream ref, so a stale fetch lets a close through: fetch before closing.

### The merge message

Built by `MergeMessage.Build` from the commits `<target>..<epic tip>` (oldest first, merges excluded) and their trailers. Example from `MergeMessageTests.Build_ListsTicketsRunsUntrackedAndForeign`:

```text
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
```

- The commit is the epic's own when its `Epic:` trailer is the epic id or its batch epic id (the squash lander stamps the batch epic id, here `42-auth`); a commit naming another epic is listed under `Commits naming another epic`.
- Each ticketed commit is one line. The leading ticket is dropped from `{ticket}: {title}` subjects (also `{ticket} - {title}`), as the squash lander's own title rule does; a subject that would become empty is kept whole (`Title_DropsALeadingTicketLikeTheSquashLander`).
- `Batch: 0` (a `squash run` landing) shows as `manual`, other values as `batch <n>`.
- `Runs: <n>` counts distinct `Swarm-Run:` values, because batch numbers restart in every run; the line is omitted when no commit has one. `Tickets: none (no Ticket: trailers found)` when there are no tickets.

## Status for orchestrators

`epic status` is the read-only view an orchestrator polls; `epic close` acts on the same assessment. `EpicStatus` fields:

| Field | Meaning |
|---|---|
| `schemaVersion` | 1 |
| `id`, `slug`, `branch`, `state` | from the record (`state` is `open` or `closed`) |
| `into` | target branch assessed (`--into`, else the epic's `baseBranch`) |
| `tip` | epic tip, null when the branch is missing |
| `ahead`, `behind` | epic commits not on `into`, and `into` commits not on the epic |
| `batchEpic` | the batch epic id, or null |
| `runs` | batch runs of this epic in the state directory (`squash run` writes none) |
| `latestRunId`, `latestRunExitCode` | the newest run and its exit code (null when unfinished) |
| `landedTasks` | task ids landed across runs |
| `openTasks` | tasks returned or unprocessed and not landed since: `task`, `state`, `branch`, `final`, `kind`, `runId`, `reason` |
| `worktrees`, `worktreesUnmerged` | managed task worktrees on the epic, and how many of them block |
| `batchRunning` | true when the per-epic lock is held by the acquire rule |
| `upstream` | the target's upstream (last fetched), or null |
| `blockers` | `code`, `detail`, `waivable` per [blocker](#close-blockers) |
| `readyToClose` | true when `blockers` is empty |

- `batch-running` and `batchRunning` cover both a `batch run` and a `squash run`: they share the per-epic lock.
- An open task's `reason` is the return's reason, except for a land-stage return without files (for example the squash lander's `requireTicket` failure), where `batch` records only `land conflict with the epic tip`: then `reason` is git's one-lined output, which carries the real cause (`LandFailureWithoutFiles_ReportsGitOutputAsReason`; returns with files keep their reason, `ReturnWithFiles_KeepsItsReason`). For what a ticket-less task costs its batch-mates under `squash.requireTicket`, see [squash-tool.md](squash-tool.md#known-limitations).

Run record, verbatim from the [dnx run](dnx-invocation-notes.md#worktree-and-epic-010) (row C29, after one `squash run` and one `batch` run had landed a task each and `worktree prune` had removed both worktrees):

```text
$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- status 42
[stdout]
{"schemaVersion":1,"epics":[{"schemaVersion":1,"id":"42","slug":"auth","branch":"epic/42-auth","state":"open","into":"main","tip":"b3a25e7fbaaef4d21975c96d827fe1a063ead533","ahead":2,"behind":0,"batchEpic":"42-auth","runs":1,"latestRunId":"20261005-122035-566-42-auth","latestRunExitCode":0,"landedTasks":["T2"],"openTasks":[],"worktrees":0,"worktreesUnmerged":0,"batchRunning":false,"upstream":null,"blockers":[],"readyToClose":true}]}
[stderr]
[exit 0, 13.0 s]
```

`runs` is 1 and `landedTasks` holds only the batch task `T2`: the `squash run` landing of `T1` left no run state, as stated above.

## Exit codes

Values are those of `ExitCodes` (the same as batch, squash and testgate).

### worktree exit codes

Source: `Program.cs` and `BaseOption`, tests in `WorktreeCliTests`.

**Output contract.** For `create` and `list`, stdout is exactly one JSON line on exit 0 and empty on exits 2 to 4. For `prune`, stdout is exactly one JSON line (the report) on exit 0 and also on exit 4 when items failed; on exits 2 and 3 it is empty. On every non-zero exit stderr carries one `error: <what>` line, optionally followed by `(<hint>)` (every `ToolException` is handled by `ToolErrors.Handle`, which writes only that line); on exit 4 from `prune` that line is `error: <n> prune item(s) failed (see items[].error in the report on stdout)`, where `<n>` is `failed`; it does not say what failed, so callers must read `done`, `branchDeleted` and `error` of each item in the stdout report (an item whose worktree was removed but whose branch was kept is among the failures). `create` warnings go to stderr as `warning: ...` lines. Never parse stderr for data.

| Exit | Meaning |
|---|---|
| 0 | Done (`create`, `list`), or `prune` with no failed item (nothing to prune is also 0) |
| 1 | Not produced by `worktree` |
| 2 | Usage or config: parse error (unknown command or option, missing argument: one `error: ... (see --help)` line), `--epic` and `--base` together, `create` with neither, invalid slug, kind, id or branch name, branch prefix not allowed, invalid config, path over 200 characters |
| 3 | Bad input: not inside a git worktree (`OutsideRepo_Exits3`), unknown or closed epic (`create`), base branch not found, branch already exists without a worktree or is checked out for another base, locked or foreign worktree at the target path, an idempotent repeat whose worktree is a stale registration |
| 4 | Environment: git failure, a non-empty directory at the target path, `create` rolled back, or `prune` with at least one item whose `error` is set, as described under [Prune rules](#prune-rules) (the stderr line is `error: <n> prune item(s) failed (see items[].error in the report on stdout)`, where `<n>` is `failed`) |
| 5 | Not produced by `worktree` |

Runs: `create 1 x` (neither option) exit 2, stderr `error: pass --epic <id> or --base <branch>`; `create 1 x --epic 42 --base main` exit 2, `error: pass only one of --epic or --base`; `create 2 x --epic 7` exit 3; `worktree nope` exit 2, `error: Unrecognized command or argument 'nope'. (see --help)`; stdout was empty in all of them.

### epic exit codes

Source: `src/Swarm.Epic.Cli/Program.cs`, `EpicOpener`, `EpicAssessor`, `EpicCloser`; tests in `EpicCliTests`.

**Output contract.** `open` and `status` print one JSON line on exit 0 and nothing on exits 2 to 4. `close` prints its JSON result on exit 0 and exit 1 only; on exits 2, 3 and 4 stdout is empty. Every `close` warning is a `warning: ...` stderr line (see [close](#close)); every non-zero exit has exactly one `error: <what>` line, optionally followed by ` (<hint>)`. Never parse stderr for data.

| Exit | Meaning |
|---|---|
| 0 | `open`, `status`; `close` with `result` `merged` or `dry-run` |
| 1 | `close` did not merge and printed why: `result` `blocked` (stderr `error: epic '<id>' not closed: <first blocker detail> (<n> blocker(s); see blockers)`) or `conflict` (stderr `error: merging '<branch>' into '<into>' conflicts in <files> (merge '<into>' into the epic and resolve, then close again)`) |
| 2 | Usage or config: parse error (unknown command, missing argument: one line, `EpicCliTests.ParseErrors_Exit2OneLine`), invalid id, slug, kind or branch name, branch prefix not allowed, invalid config, close worktree path over 200 characters |
| 3 | Bad input: not inside a git worktree, unknown epic (`status <id>`, `close`), epic already closed (`close`), conflicting epic record or existing branch (`open`), missing base (`open`) or target branch (`status`, `close`) |
| 4 | Environment: git failure; the epic lock taken by a run after the first check; the target moved, is being rebased, cannot be locked, already contains the epic, or is not at the merge commit after the update; a failed fast-forward (untracked or ignored files in the way, or a file held open, after which the error lists the files to restore); a failed merge or temporary worktree; the record could not be saved after the merge (the error says so and how to fix the record by hand); `open` could not save the new record (the branch is deleted again) |
| 5 | Not produced by `epic` |

## Windows notes

- **200-character guard.** Task worktree paths (`<worktreeRoot>/t-<ticket>`) and the close worktree (`<worktreeRoot>/close-<id>`) go through `StatePaths.Guard` (200 characters, exit 2 before anything is created), the same guard as the run-state paths of `batch`.
- **MAX_PATH warning.** `worktree create` warns when the worktree path plus the longest tracked path exceeds 259 characters (see [create](#create)).
- **Path normalisation.** Worktrees are identified by their branch's `swarm-*` metadata, not by path; where paths are compared (registration checks, the close worktree), `WorktreeList.SamePath` compares `Path.GetFullPath` results without a trailing separator, case-insensitively on Windows (so `C:/x` and `c:\X\` match; git prints forward slashes).
- **Files held open.** A process holding a file in a task worktree makes its prune fail per item ([Prune rules](#prune-rules), held file); in the close worktree it leaves the directory behind with a warning in the close result, after the merge has already succeeded or the conflict has been reported.
- **`git worktree prune` is repository-wide, so no tool runs it.** `worktree prune` and `epic close` remove one worktree at a time; `worktree create` and `batch`/`squash run` remove only the stale registration of their own path. A `git worktree prune` you run yourself (or the one `git gc` runs for registrations older than `gc.worktreePruneExpire`, three months by default) drops every unlocked stale registration, including one whose directory still exists.

## Known limitations

From the review ledger. These were not fixed in this plan. "Review ruling" numbers (C2, C3, C6) are the review ledger's rulings, not the plan's global constraints C1 to C11 that the sections above cite (for example the held-file note under C6 prune safety).

- **The tools never clear a stale registration whose directory still exists.** A worktree that git calls prunable (for example its `.git` file is gone) while its directory still exists is kept with every `prune`, also with `--force`, because the directory may hold the only copy of work. It is reported `registration stale (gitdir missing) but directory exists; inspect it`. Remove or repair the directory yourself (`git worktree repair <path>` restores a missing `.git` file); the next `prune` then handles it normally, or as a missing-directory item. It does not necessarily stay: a `git worktree prune` outside the tools (or `git gc` after `gc.worktreePruneExpire`) deregisters it, after which `list` and `prune` no longer see it and its branch stays, as after a held-file failure.
- **A held-file failure leaves a directory and a branch that later prunes never revisit** (Ruling C2: an orphan sweep is out of scope for this plan). After the failure above git has already deregistered the worktree, so `list` and `prune` (which enumerate registered worktrees only) never see it again, and the task branch and its `swarm-*` metadata stay. A `git worktree prune` run outside the tools deregisters worktrees whose directory was deleted by hand in the same way (the tools themselves only remove the stale registration of their own path). Delete the directory and the branch yourself (`git branch -D`); until a later plan adds a sweep for managed branches without a worktree, such branches accumulate.
- **Locked worktrees are never removed**, not even with `--force`. Unlock them (`git worktree unlock <path>`) first.
- **Unmanaged worktrees are never touched.** A worktree without the branch metadata (made by hand, or the `int-<epic>` integration worktree of `batch`) is invisible to `prune`.
- **`RunDirectories.Prune` keeps the newest 20 finished runs across all epics** (Ruling C3; a limitation of the `batch` run-state code, not of `worktree`). A busy epic can delete another epic's run folders and with them the evidence behind the `tasks-returned` close blocker and the `ledger` merge check. The fix is to prune per epic; it is not done.
- **The exit-code mapping counts kept branches as failures.** A forced or merged removal whose branch could not be deleted is `done: true` with an `error`, counted in both `removed` and `failed`, and exits 4 (see [Prune rules](#prune-rules)).
- **`--force` discards uncommitted files** of any managed, unlocked worktree whose directory is present, including one whose branch is checked out outside the managed root (a design question left open in the review; from the code, not run).
- **Epic merge commits bypass commit hooks** (review ruling C6, for the human). `epic close` makes its merge with `--no-verify` and `--no-log`, as `batch`'s integration merges use `--no-verify`, so local `commit-msg` and `pre-merge-commit` policies never see it. The alternative is to keep the hooks and accept exit 4 when a hook rejects the merge; it was not chosen.
- **A crashed local run blocks `epic close` until its lock expires.** `batch-running` follows the lock's acquire rule (a fresh heartbeat always counts) and is not waivable; wait until the lock is older than `expirySec`. The staleness check uses the file config's `expirySec` even if the run used a longer `--expiry-sec` override (from the code).
- **`RunDirectories.Prune` across epics** (review ruling C3, above) also affects `epic`: the `tasks-returned` and `run-unfinished` blockers, `runs` and `landedTasks` see only the run folders that survive the newest-20 prune, so a busy epic can make another epic's returned tasks disappear from its status. The fix (per-epic pruning) is for the testgate + batch code.
- **Point-in-time view.** `epic status` is a read; a run can start right after it. `epic close` therefore takes the lock and re-assesses itself.
- **Rebase windows.** The rebase guard checks just before the target moves, so a rebase started in the instant between that check and the move is not caught (from the code and the review, not run). `--delete-branch` checks for a rebase of the epic branch in the same way, so the same instant applies there.
- **No remote.** `active-behind-upstream` uses the last fetch; `epic close` never pushes the merge.
- **Line endings (fixed in the Plan C final-review fix wave).** The tools run git with `-c core.autocrlf=false` (`GitRunner.BaseConfig`). The dnx run found that, with Git for Windows' default `core.autocrlf=true`, a tracked file git itself checked out with CRLF (for example after `git checkout -- <file>`) then read as modified, so `active-dirty` blocked every close ([run 1 output](dnx-invocation-notes.md#code-bug-found-line-endings-make-active-dirty-fire-on-a-clean-worktree)). Where git judges or updates a working tree the user owns, the tools now keep the repository's own line-ending settings (`GitRunner.WithRepoLineEndings`): the `active-dirty` check, `worktree list`'s `dirty` flag (and so `prune`), the non-forced `git worktree remove` of `prune`, and the fast-forward of the target's checkout by `epic close` (`CleanCrlfCheckoutUnderRepoAutoCrlf_IsNotActiveDirty`, `List_CleanCrlfCheckoutUnderRepoAutoCrlf_IsNotDirty`, `MergedCleanCrlfCheckoutUnderRepoAutoCrlf_IsRemovedWithoutForce`, `CleanCrlfCheckoutUnderRepoAutoCrlf_FastForwardsAndWritesTheUsersLineEndings`; each test sets repo-local `core.autocrlf=true`). Tool-owned worktrees (the integration worktree of `batch`, the squash lander's, the temporary close worktree) keep `core.autocrlf=false`, and `worktree create` still checks out task worktrees with LF (not changed; the dnx run saw `LF will be replaced by CRLF` warnings there). Not re-run with dnx after the fix.
- **A kill between the target's move and the record save leaves the epic merged but recorded open** (from the code, not run; not fixed). `epic close` moves the target, verifies it, then saves the record closed. If the process dies in between, the target holds the merge but the record says `open`, and every later `epic close` is blocked by `nothing-to-merge` (not waivable). Fix by hand: find the merge commit (`git log --first-parent --merges -1 --format=%H --grep "^Merge epic <id> " <into>`), then in `<state>/epics/<id>.json` set `"state": "closed"`, `"mergeCommit": "<sha>"`, `"mergedInto": "<into>"` and `"closedUtc"` (an ISO-8601 UTC time, for example `"2026-10-05T12:00:00Z"`). The epic branch is kept; delete it yourself if wanted.
- **Not run.** git older than 2.38 (the content check), non-Windows hosts, and the 259-character path warning end to end (a unit test pins it).
