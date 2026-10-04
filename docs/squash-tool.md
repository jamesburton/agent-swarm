---
created: 2026-10-04
updated: 2026-10-04
status: current
---
# squash

Lands task branches on an epic branch as one trailer-stamped commit per task (or per ticket inside a stack), with exactly the tree that `batch` tested. It runs in two ways: inside `batch` (the default lander since `Swarm.Batch` 0.2.0) and as the `squash run` command for one branch by hand. Plan: [2026-10-03-squash.md](plans/2026-10-03-squash.md). Decisions: [decisions.md](decisions.md#squash-lander-production-plan-2026-10-04). Companion tools: [batch-tools.md](batch-tools.md).

Written on 2026-10-04 from the source at the end of the squash plan. Each statement was checked by reading the named source or by running the command; anything else is labelled `unverified`. How each was checked is in the plan's Task 8 report.

## Package status (NOT REAL)

- **NOT REAL: nothing is published.** `Swarm.Squash` returned 404 on nuget.org on 2026-10-03 (see [definition-format.md](definition-format.md)). Nobody owns the id, so anyone could publish under it and `dnx` would download and run it (dependency confusion). Run it only with `--add-source <your feed>`.
- Before any publish: reserve an owned id prefix and add license, authors and readme metadata. `dotnet pack` currently prints `The package Swarm.Squash.0.1.0 is missing a readme` (NU5039); no metadata was added.
- Versions as built: `Swarm.Squash` 0.1.0, `Swarm.Batch` 0.2.0, `Swarm.TestGate` 0.1.1. dnx caches an extracted version under `~/.nuget/packages/<id>/<version>` and does not pick up a re-pack of the same version: bump `<Version>` on every re-pack (see [dnx-invocation-notes.md](dnx-invocation-notes.md#gotcha-stale-tool-cache)).
- Requires the .NET 10 SDK or later. dnx rules: no `--yes`, `dnx.cmd` in Git Bash, `--` before the tool's own arguments. Run records: [dnx-invocation-notes.md](dnx-invocation-notes.md#companion-tools).
- The renderer sample's runbook step `dnx Swarm.Squash@0.1.0` has no arguments. It exits 2 with `error: Required command was not provided. (see --help)` and changes nothing (run, see the dnx notes). In a batch flow the squashing happens inside `batch`, not in that step.

## Two ways to use it

1. **Inside `batch`.** `"lander": "squash"` is the default, so `batch run tasks.json` lands each green task as a squashed commit. `"lander": "fast-forward"` keeps the old behaviour (see [batch-tools.md](batch-tools.md#the-ilander-contract)).
2. **`squash run`** squashes one branch by hand onto its epic (see [`squash run`](#squash-run)).

## What lands

For each task, one commit on the epic branch whose parent is the previous epic tip and whose tree is the tree of the tested integration chain after that task's merge. Example (the exact string pinned by `SquashMessageTests.Build_MultiCommit_ListsCommitsAndStampsTrailers`; `Bob` is a second author):

```text
9933: add parser

Squashed commits:
- 9933: add parser
- fix parser

Ticket: 9933
Epic: E1
Batch: 3
Swarm-Run: run-1
Task: T1
Source-Commit: abc123
Co-authored-by: Bob <bob@example.invalid>
```

| Trailer | Value |
|---|---|
| `Ticket` | the ticket (see [Tickets](#tickets)) |
| `Epic` | epic id |
| `Batch` | batch number; `0` for a manual `squash run` |
| `Swarm-Run` | run id |
| `Task` | task id, once per task in the commit, in landing order |
| `Source-Commit` | the tested tip of the task's branch, after its `Task:` line; omitted for a task whose merge was a no-op |
| `Co-authored-by` | `Name <email>` of each other author, distinct by email (case-insensitive), excluding the author and the tool |

The trailers are one paragraph at the end in this fixed order (`Source-Commit` follows its own task, so a stack gives `Task: T1`, `Task: T2`, `Source-Commit: <T2's tip>` when T1 was a no-op). Messages are LF with a trailing newline. Source: `src/Swarm.Squashing/SquashMessage.cs`.

- **Subject.** `squash.subjectTemplate` (default `{ticket}: {title}`), expanded in one pass so values are never expanded again, collapsed to one line. The title is the subject of the oldest squashed commit with a leading ticket followed by `:`, space or `-` removed (`99330 bigger number` keeps its number for ticket `9933`), else the first task's branch name. The `Squashed commits:` list (one `- <subject>` per original non-merge commit, oldest first) appears only when there is more than one commit.
- **Grouping.** One commit per task. Inside one stack (tasks linked by `dependsOn`), consecutive members with the same ticket (case-insensitive) share one commit. Separate stacks never share a commit, even with the same ticket, so two independent tasks of one ticket give two commits with the same `Ticket:`.
- **Stacks.** A stack lands whole or not at all (see [Guarantees](#guarantees)).
- **Empty tasks.** No empty commits. A task whose merge was a no-op (nothing new) or whose net tree change is zero counts as landed at the commit that already holds it. `squash run` prints `"empty": true` and exits 0.
- **Identity.** The committer is always the tool identity `swarm-batch`. `squash.author` `original` (default) records the author of the oldest squashed commit and every other distinct author becomes `Co-authored-by`; `tool` records the tool identity and every original author becomes `Co-authored-by`. Dates are "now". Inherited `GIT_AUTHOR_*` and `GIT_COMMITTER_*` variables are removed for the commit. No hooks run and nothing is signed (`commit-tree` runs no hooks; the tool also passes `-c commit.gpgSign=false`).

## Guarantees

All verified by reading `SquashLander.cs`, `TreeGuard.cs`, `TestedChain.cs` and by the `Swarm.Tools.Tests` suite.

- **Tree from the tested chain.** Each commit is built with `git commit-tree <tree of the tested chain after the task's merge> -p <previous tip>`. Nothing is merged twice, nothing is checked out and `merge --squash` is not used. `TestedChain` reads the chain from the tested commit: the tested commit must be the epic tip plus exactly one integration merge per task (subject `batch: merge <id> (<branch>)`); anything else is exit 4 and nothing lands.
- **Tree check before the ref moves.** `TreeGuard` compares the tree of the rebuilt tip with the tree of `TestedCommit` when nothing failed (or the tree after the last landed task when a task failed). Tree-id equality is the exact form of `git diff --exit-code`. On a mismatch the tool exits 4 with `git diff --stat` in the message and the epic is not moved (the new commits stay unreferenced).
- **Request-order contract.** The lander requires the request's tasks in the tested chain's order with every stack contiguous. Otherwise it exits 4 (`task 'X' is out of order: its stack is not contiguous in request order ... nothing landed`) rather than credit a task with another's content.
- **One compare-and-swap.** Whenever the tip changed, one `update-ref <epic> <new tip> <tip before>` moves the epic. If the epic moved meanwhile: exit 4, `epic branch '<b>' moved during the run; nothing landed for batch <n>`.
- **Task branches are never modified** and no refs are created.
- **Stop at the first failure; a stack lands nothing.** Tasks land in request order. At the first failing task the failure is reported, every other member of that task's stack and every later task is reported as not attempted, and the epic moves only to what landed before the failing stack. `batch` returns the failed task for a worker (stage `land`) and lands the rest.
- **No-op tasks.** Counted as landed without a commit.
- **`requireTicket`.** See [Tickets](#tickets).

## Tickets

Resolution order for each task (`TicketResolver`):

1. The explicit `--ticket` (`squash run` only), trimmed. The CLI requires it to be one token without whitespace or control characters (exit 2).
2. The first match of `squash.ticketPattern` (named group `ticket`) on the branch name, then on the task id.
3. The task id, unless `squash.requireTicket` is true; then the task fails to land with `no ticket for task '<id>' (branch '<b>'): squash.ticketPattern '<p>' matches neither and squash.requireTicket is true`. A match counts only when the group is non-empty and has no whitespace or control characters.

The default pattern is `(?:^|/)(?<ticket>\d+)(?:-|$)`. Examples from `TicketResolverTests` (branch, task id, ticket):

| Branch | Task id | Ticket |
|---|---|---|
| `feature/9933-squash-tool` | `T1` | `9933` |
| `bugfix/9920-multi-node` | `T1` | `9920` |
| `task/9933` | `T1` | `9933` |
| `task/T1` | `9940` | `9940` (from the id) |
| `task/T1` | `T1` | `T1` (the id itself) |
| `epic9933/x` | `T7` | `T7` (`9933` is not at a path boundary) |

A non-numeric ticket needs a configured pattern, for example `(?<ticket>[A-Z]+-\d+)` gives `ABC-12` for `task/ABC-12-fix`. A pattern that takes over one second on an input fails with `squash.ticketPattern timed out on '<input>'` (exit 2).

**`requireTicket` and no-op tasks (for the human to decide).** The ticket check runs before the empty check. Under `requireTicket: true`, a task without a derivable ticket therefore fails its whole stack even when its merge was a no-op and nothing would have been committed. `batch` then returns that task for a worker for no content. The alternative, checking for emptiness first, would let a ticket-less no-op task land silently. Which behaviour is wanted has not been decided; the code does the former (`SquashLander.LandUnit`) and no test pins the no-op case. Leave `requireTicket` off if this matters.

**Rebased copies.** When `batch` lands a task through its rebase copy `rebased/<epic>/<task id>`, the copy's name no longer holds the ticket. The lander reads the copy's reflog (`git reflog show --format=%gs refs/heads/<copy>`) and takes the first line starting `branch: Created from refs/heads/` or `branch: Reset to refs/heads/`; the rest is the worker branch used for the ticket. Observed with git 2.54 on Windows: newest first, `rebase (finish): ...` then one of those two lines (`RebasedCopy_KeepsWorkerBranchTicket` passes). If the reflog has no such line (for example `core.logAllRefUpdates=false`), the copy's own name is used, so the ticket falls back to the task id or fails under `requireTicket`. That fallback was read in the source and not run.

## Configuration

Both `batch` (when it lands) and `squash run` read the shared `.swarm/batch.json`; lookup, strict keys and the config error format are as in [batch-tools.md](batch-tools.md#configuration). Squash keys:

| Key | Default | Rule |
|---|---|---|
| `lander` | `"squash"` | `"squash"` or `"fast-forward"` |
| `squash.ticketPattern` | `(?:^\|/)(?<ticket>\d+)(?:-\|$)` | a valid regular expression with a named group `ticket` |
| `squash.requireTicket` | `false` | boolean |
| `squash.subjectTemplate` | `{ticket}: {title}` | one non-empty line; placeholders `{ticket} {title} {taskId} {taskIds} {branch} {epic} {batch} {runId}`; `{taskIds}` joins ids with `+` |
| `squash.author` | `"original"` | `"original"` or `"tool"` |

Old files without the new keys stay valid (`OldFileWithoutNewKeys_StillValid`). `baseBranch` (default `main`) is also used here: it bounds the epic history scanned for already-landed `Source-Commit:` trailers (`<baseBranch>..<epic>`, first-parent). Example:

```json
{
  "lander": "squash",
  "squash": { "ticketPattern": "(?<ticket>[A-Z]+-\\d+)", "requireTicket": true, "subjectTemplate": "[{ticket}] {title}", "author": "tool" }
}
```

Every message, from `SquashConfigTests` (each is exit 2, one line, `<source>: <message> (see docs/batch-tools.md#configuration)`; the hint text points at the batch page for squash keys too):

| Message (starts with, after the source) | Cause |
|---|---|
| `lander must be 'squash' or 'fast-forward' (got 'rebase')` | other lander name |
| `squash.ticketPattern must be a valid regular expression with a named group 'ticket' (got '...')` | pattern does not compile, or has no `ticket` group |
| `squash.subjectTemplate must be one non-empty line` | empty, whitespace only or contains a line break |
| `squash.subjectTemplate has unknown placeholder '{tikcet}' (known: {ticket}, {title}, ...)` | placeholder not in the list |
| `squash.author must be 'original' or 'tool' (got 'me')` | other author mode |
| a message naming the unknown key, for example `'tiketPattern'` | unknown key in `squash` |
| `invalid config ...` | `"squash": null` (also other `null` values) |

**Azure DevOps branch names.** The epic branch name comes from `epicBranchTemplate`. For `example-org` repositories the pipelines match only `feature/` and `bugfix/` prefixes, and shortened prefixes such as `feat/` or `fix/` silently break pipeline triggers. The tool does not check the prefix. Use, for example:

```json
{ "epic": "9933-squash-tool", "epicBranchTemplate": "feature/{epic}" }
```

which gives the branch `feature/9933-squash-tool` (pinned by `Run_FeaturePrefixedEpicBranch`). The `batch` tool's task branches are whatever the tasks file names; for `bugfix/` epics use `"epicBranchTemplate": "bugfix/{epic}"`.

## `squash run`

```text
squash run --task ID --branch BRANCH [--epic ID] [--ticket TICKET] [--run-id ID]
           [--config FILE] [--state DIR] [--slots N] [--max-wait SEC] [--verbosity quiet|normal|detail]
squash --version
```

(Usage lines from `--help` of the built tool.) `--task` and `--branch` are required; `--task` is a safe name (letters, digits, `_`, `-`, single dots), `--branch` a valid branch name that must exist and is never modified. `--epic` defaults to the config `epic`; the epic branch (`epicBranchTemplate`) must exist and must not be checked out in any worktree. `--run-id` defaults to `squash-<yyyyMMdd-HHmmss-fff>-<epic>`. `--slots` and `--max-wait` are accepted but do not affect the epic lock (read from source: the lock is taken for one slot without waiting).

What it does: takes the epic lock, creates (or reuses) the integration worktree `<worktreeRoot>/int-<epic>`, merges the branch onto the epic tip there, and calls the same `SquashLander` as `batch`, with `Batch: 0`. There is no test run: the tree it lands is the tree of that one merge. Progress goes to stderr; stdout is exactly one JSON line, `SquashRunResult`:

`schemaVersion`, `runId`, `task`, `branch`, `epic`, `epicBranch`, `epicTipBefore`, `epicTipAfter`, `ticket` (null when nothing landed), `commit` (null when nothing landed or empty), `empty`, `exitCode`, `note` (why nothing landed, or null).

| Exit | Meaning (source: `SquashRunner`, `Program`; tests in `SquashCliTests`) |
|---|---|
| 0 | Landed, or empty (the epic already had the change; `empty: true`, `note` says so) |
| 1 | Merge conflict with the epic tip (nothing lands, `note` names the files), or a land failure such as `requireTicket` with no ticket |
| 2 | Usage or config: no or unknown command, missing `--task`/`--branch`, bad `--task`/`--ticket`/`--branch`/run id, invalid config, ticket pattern timeout |
| 3 | Bad input: not in a git worktree, task branch not found, epic branch missing or checked out |
| 4 | Environment: epic lock held, epic moved during the run (also after an empty run), tree mismatch, chain error, git failure |
| 5 | Defined for a testgate wait timeout; `squash run` takes no test slot and does not produce it (`unverified` at runtime) |

**Shared epic lock.** `squash run` and `batch` use the same per-epic lock `<state>/locks/batch-<epic>` and the same integration worktree, so they never run on one epic at once. A held lock is exit 4: from `squash run` the message is `another batch or squash run holds epic '<epic>'`; from `batch` it is `another batch run holds epic '<epic>'` even when the holder is a `squash run` (the wording is from the batch code and says only "batch").

Run record (Windows 11, .NET 10, `dnx.cmd` with a local feed, on 2026-10-04; full output in [dnx-invocation-notes.md](dnx-invocation-notes.md#companion-tools)): `squash run --task T1 --branch task/9933-one` landed `9933: add one` with author `Ada` and `Ticket: 9933`; a following `Swarm.Batch@0.2.0` run landed `task/9934-two` as `9934: add two` by `Bob`, and `git rev-list --merges --count main..epic/E1` printed `0`.

## Windows notes

- **Messages and output are UTF-8.** The message goes to `commit-tree -F <file>` (UTF-8 without BOM, LF; never through argv), and git output is decoded as UTF-8. A repository with another `i18n.commitEncoding` is `unverified`.
- **No checkout.** Commits are built from trees, so CRLF conversion, the executable bit and case-only renames land exactly as tested (the tree check compares tree ids). Pinned by `SquashLanderEdgeTests.CrlfBytesAndExecutableBit_Preserved` and `CaseOnlyRename_LandsExactTree`.
- **Environment.** Inherited `GIT_AUTHOR_*` / `GIT_COMMITTER_*` are ignored (removed for the commit).
- **Command-line length.** The list of already-landed source commits excluded from an author/commit-list scan is passed to git in chunks of at most 24,000 characters, well under the Windows 32,767-character limit.
- **Paths and locks.** Path guard (200 characters) and lock behaviour are those of [batch-tools.md](batch-tools.md#windows-notes). The message file `SWARM_SQUASH_MSG` lives in the integration worktree's git dir and is deleted afterwards.

## Known limitations

From the review ledger (deferred minors that affect users); none has a fix in this plan.

- **Ticket override.** The lander API only trims an explicit ticket. `squash run` validates `--ticket` (one token, no whitespace or control characters), so this matters only to code that calls `SquashLander` directly.
- **Co-author truncation.** Each trailer value is cut to 400 characters. A very long `Co-authored-by` can lose the end of its email. A name containing `<` or `>` is not sanitised. Co-authors are de-duplicated by email, so authors with an empty email collapse into one.
- **Stale git error text.** `EpicRef.Move` drops git's stderr: any failure of the compare-and-swap (for example `cannot lock ref`) is reported as `moved during the run`.
- **Lock wording.** `batch` says `another batch run holds epic` when a `squash run` holds the lock.
- **Stacked on a rebased copy.** A branch stacked on one that `batch` landed through a rebased copy records the copy's commit in `Source-Commit:`, so the original commits are not recognised as already landed. Its author and `Squashed commits:` list can then include them.
- **Missing task branch in the chain.** A task branch that vanishes between testing and landing is reported as `not contained` without distinguishing a missing branch from other git failures.
- **Squash section echoes.** Config errors echo the raw `ticketPattern`/`author` value; `{task1}`-style placeholders with digits are not recognised as placeholders and are expanded literally (unknown letters-only names are rejected).
- **Config hint.** Config errors from the squash keys point at `batch-tools.md#configuration`.
- **Not run.** `unverified`: non-ASCII author names and subjects end to end, a repository using `core.logAllRefUpdates=false`, and the `requireTicket` plus no-op case above.
- **Out of scope.** Merging the epic into the active branch, changing the renderer sample's `tool:squash` step, commit signing, and publishing.
