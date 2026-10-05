---
created: 2026-10-02
updated: 2026-10-05
status: current
---
# Decision log

Newest last. Each entry: decision, rationale, date. Revisit by adding a new entry, not editing history.

## Stage 1 — foundations (2026-10-02)

| Decision | Detail |
|---|---|
| Pilot | Synthetic sandbox repo first (slow, measurable test suite), then repeat on one real repo. |
| Tool style | Spike as file-based C# (`dotnet run x.cs`); promote survivors to NuGet `dnx` tools. |
| Portability | Claude-first; definition is tool-neutral so renderers for Codex/Gemini can follow. |
| Orchestration | Support both; deterministic Workflow scripts by default, LLM orchestrators for judgement steps, chosen per node in the definition. |

## Stage 2 — definition, outputs, state, sign-off (2026-10-02)

| Decision | Detail |
|---|---|
| Definition format | **All three**, not one: Markdown + light front-matter (primary, pasteable), YAML, and C# file-based. Build one canonical in-memory model with a front-end per format, so each is developed in parallel from the start; Markdown is the reference. Fall back to staging them only if parallel work proves costly. |
| Outputs | Primary: generated agent files (`.claude/agents/*.md`) plus a Workflow script. Near-term follow-on: a `swarm run` interpreter that runs a definition directly (absorbing native agent-team style features). *(Assumption: "near-team" read as "near-term".)* |
| Run state | Configurable: repo-local `.docs/runs/` (**default**) or a shared directory outside the repo. `.docs/` stays git-ignored (verified), alongside other private notes such as handoffs. Worktrees resolve the **main** worktree's `.docs/` (via `git rev-parse --git-common-dir`) so all tasks share one run state. |
| Sign-off | All four in v1: policy rules / permission mode, `PermissionRequest` hooks, machine-evidence gates (checked by code, not agent claims), and human sign-off via Remote Control / channel. Agent-to-agent messages never authorise actions. |

## Stage 3 — test and merge mechanics (2026-10-02)

| Decision | Detail |
|---|---|
| Squash timing | At batch-green: task branches stay disposable until the batch passes; the epic branch only receives tested, ticket-sized commits in queue order. |
| Merge strategy | Sequential, stop-on-conflict into the integration worktree. The first conflict names the offending pair; that task returns to its worker (rebased on integration state) and the rest continue. |
| Worker-local tests | Project-graph affected tests: map changed files to owning projects and dependents, run only those test projects. Deterministic, no LLM tokens (.NET first). |
| Batching | Adaptive batch size (grows after green, shrinks after red, bounded by min/max and the CPU slot budget); red batches are bisected by halving (~log2 N extra runs) and greens land as they pass. |
| Defaults (mine, unconfirmed) | Slot lock = lock directory with atomic create plus heartbeat/expiry, under the run-state location. |

## Stage 4 — packaging, roles, doc sweep, wiki (2026-10-02)

| Decision | Detail |
|---|---|
| Packaging | A Claude Code plugin (skills, agents, templates, workflows; text-only, versioned) plus independently versioned `dnx` tool packages that the plugin calls. |
| Starter roles | Worker (implementer), orchestrator / sub-orchestrator, expert / escalation, reviewer + test-triage. |
| Doc sweep | All three: on-read check, scheduled sweep (proposes `[STALE?]` marks/bumps/archives for review), and a manual `dnx` command usable as a lint/CI check. Per-type staleness thresholds to be proposed in the doc-lifecycle spike. |
| Wiki | Azure DevOps code wiki published from `docs/`; keep docs ADO-wiki compatible (relative links, no private details, an index/order file). |

## Stage 5 — spikes and review (2026-10-02)

| Decision | Detail |
|---|---|
| Spikes | All four: batched-test simulator + bisect; definition model + Markdown renderer; worker → expert hand-off; doc-lifecycle sweeper. |
| Variants | Two built and measured per spike (A/B); a third option described only. |
| Location | `spikes/NN-name/` on `main`, each labelled throwaway with a README (question, how to run, results, verdict). |
| Execution | Parallel Sonnet agents, one worktree per variant; I integrate and review. |

## Spike review (2026-10-02)

| Spike | Decision | Notes |
|---|---|---|
| 0 probes | Results recorded | See `spikes/00-probes/RESULTS.md`; no `--yes` on `dnx`; use `dnx.cmd` under Git Bash. |
| 1 batched tests | **Adopt B** (adaptive size 2-8 + file-overlap pre-batching + halving bisect) | Chosen over A despite 1-2 more runs on the non-overlapping sandbox, for conflict-prone real work. Still to test: merge-conflict path and real overlapping edits. |
| 2 definition renderer | **Adopt A** (typed canonical model + front-ends) | Generated Workflow scripts still only syntax-checked; run one in the real Workflow tool before relying on it. |
| 3 expert hand-off | **Keep both A (structured) and B (pointer-based)** as swappable options chosen per swarm definition | Rerun on harder scenario, 12 graded runs: all correct (35/35), cost within ~10%; A cut Opus tool calls 12.5 → 7, B cost most, cold Sonnet cheapest. Evidence does not favour a winner; cold escalation remains a valid option. Next evidence needed: a real weak-worker transcript on a larger repo. |
| 4 doc sweeper | **Adopt A** (deterministic); fix this repo's docs now | Done: front-matter added to 5 docs, orphans linked from AGENTS.md, statuses normalised; sweep now 0 errors. B's `--llm` check stays an optional follow-up. |

## Spike 1 completion (2026-10-03)

Closed with evidence in `spikes/01-batched-tests/b/RESULTS.md`: measured serial 12 suites / 889 s vs batched 2 suites / 155 s (load caveat); real conflict path (10 landed, 3 returned, 1 rebased, 2 needs-worker); touches derived from git; concurrent batches serialised with no deadlock; 6/6 edge cases. Still unverified: real repo, two simultaneous reclaimers, more than two gates.

| Decision (promotion of testgate/batch) | Choice |
|---|---|
| Missing task branch | Return that task (bad input) in `returned.json`, run the rest; exit 1 at the end. |
| Rebase policy | Auto-requeue a rebased copy ref (`rebased/E1/Txxx`), max 1 attempt; second conflict goes to needs-worker; worker branch left intact. |
| Stacked tasks | Land a stack as one unit: tasks declare `dependsOn`; batched together and squashed per ticket in order. |
| Blame and pre-batching | Later task in queue order goes back (convention, not true attribution); keep file-name pre-batching as a cheap hint. |
| Other PROMOTION.md items (exit codes, config file, JSON schema, logging, gate scope, wait policy, exit 0 after rebase) | Spike defaults, to be confirmed in the production plan. |

## Spike 2 completion (2026-10-03)

Real Workflow dry runs (2 runs, 10 agents total) proved the control flow, including escalation to an expert. Eight renderer/design findings are recorded in `spikes/02-definition-renderer/README.md`; the production plan must address them. Key design consequences: (1) deterministic tool steps (testgate, batch, squash) run outside the Workflow script (it cannot exec; an LLM gate costs ~31k tokens), the workflow only fans out LLM work and reads gate evidence; (2) tool references must be explicit pinned package ids, never bare names; (3) no `--yes` on `dnx`; (4) generated agent files must exist at session start.

## Testgate + batch production plan (2026-10-04)

Plan: [2026-10-03-testgate-batch.md](plans/2026-10-03-testgate-batch.md). Tool reference: [batch-tools.md](batch-tools.md). Confirms the remaining spike-1 promotion defaults:

| Decision | Choice |
|---|---|
| Exit codes | 0 ok, 1 returned, 2 usage/config, 3 bad input, 4 environment, 5 gate wait timeout; testgate maps a failed child to 1 (child code in JSON). |
| Exit after rebase | 0 when every task landed, including rebased copies. |
| Config | `.swarm/batch.json` in the main worktree; flags win; strict keys; validated on load. |
| JSON schema | `schemaVersion: 1` everywhere; `returned.jsonl` append-only, last line per task wins. The "Spike 1 completion" entry says `returned.json`: it was renamed `returned.jsonl` (one appended line per change). |
| Logging | stderr progress with `--verbosity`; `events.jsonl` per run; per-suite logs; keep the newest 20 finished runs. |
| Gate scope | One machine, lock files in the state dir; Windows closes the two-reclaimer race. |
| Wait policy | Polling, no FIFO; `maxWaitSec` 3600 by default, then exit 5. |
| Lander | `ILander` seam; default fast-forward to the tested commit until the squash lander (Plan B). |
| `--base` flag | The PROMOTION.md `batch run --base` flag was dropped: `baseBranch` stays a config key and is consumed only by epic creation in a later plan (Plan C). |
| Packaging | Placeholder ids `Swarm.TestGate` / `Swarm.Batch`, version 0.1.1, built and run through `dnx` from a local feed only; nothing is published and a real id prefix plus license/authors/readme metadata are still to be decided. |

## Squash lander production plan (2026-10-04)

Plan: [2026-10-03-squash.md](plans/2026-10-03-squash.md). Tool reference: [squash-tool.md](squash-tool.md). The rulings below are as built (S1 to S12 of the plan's Global Constraints); where the build differs from the plan's text, the build is recorded.

| # | Decision | Choice as built |
|---|---|---|
| S1 | Content | Each squashed commit takes its tree from the tested integration chain (`commit-tree`, no checkout, no re-merge); nothing is merged twice. |
| S2 | Tree guarantee | `TreeGuard` compares tree ids with the tested commit (or the last landed task's chain state after a failure) before the epic moves; a mismatch is exit 4 and the epic is not moved. Batch's own post-land check remains a second guard. |
| S3 | Granularity | One commit per task; consecutive same-ticket members of one stack share a commit; separate stacks never share one. |
| S4 | Stacks | A stack lands whole or not at all (`Failure` plus `NotAttempted` for its other members and later units). The lander requires request order with contiguous stacks, else exit 4. |
| S5 | Tickets | `--ticket`, else `squash.ticketPattern` on the branch then the task id, else the task id. With `requireTicket`, tickets are resolved for every task that needs a commit before anything is built; if one is missing nothing lands (that task fails, every other task is not attempted and is retested without it), so the epic never receives an untested prefix. A no-op task needs no ticket: the empty check runs first (Ruling B3-final). A rebased copy uses the worker branch from its reflog. See [squash-tool.md](squash-tool.md#tickets). |
| S6 | Message | Templated subject (`{ticket}: {title}`), a commit list when there is more than one, fixed trailers `Ticket`, `Epic`, `Batch`, `Swarm-Run`, `Task`, `Source-Commit`, `Co-authored-by`. |
| S7 | Identity | Committer `swarm-batch`; author = oldest original author (or the tool), others as `Co-authored-by`; no hooks, no signing; inherited `GIT_AUTHOR_*`/`GIT_COMMITTER_*` removed; commits of already-squashed branches are excluded through `Source-Commit:` trailers (bounded by `baseBranch`, chunked). |
| S8 | Empty tasks | No empty commits; counted as landed. |
| S9 | Epic move | One compare-and-swap `update-ref` per land when the tip changed; an all-empty `squash run` re-reads the ref and exits 4 if the epic moved. A failed `update-ref` re-reads the ref: `moved during the run` only when the tip changed, else `could not move epic branch` with git's reason and a stale-lock hint (shared by both landers). |
| S10 | Default lander | `squash` (config key `lander`; `fast-forward` remains); `Swarm.Batch` 0.2.0; `Swarm.Squash` 0.1.0, NOT REAL placeholder id, nothing published. |
| S11 | Batch wiring | `Program.Run(..., Func<SwarmConfig, ILander>)` overload added; `Main` passes `Landers.Create`. |
| S12 | `squash run` | One task; shares the per-epic lock and the integration worktree with batch; `Batch: 0`; exit 0 landed or empty, 1 conflict or land failure, 2 usage or config, 3 bad input, 4 environment. |

Cross-plan facts:

- The `ILander` contract and `LandRequest`/`LandResult`/`LandedTask`/`LandFailure` are unchanged from the testgate + batch plan; the `Program.Run(args, stdout, stderr, cwd, ILander)` overload is unchanged and the `Func<SwarmConfig, ILander>` overload is the only addition.
- `GitRunner` gained `WithEnvironment`; `SwarmConfig` gained `lander` and `squash` (old files stay valid, unknown keys still rejected); `baseBranch` is now used by the squash lander.
- Azure DevOps (`example-org`): epic branches must be `feature/` or `bugfix/` through `epicBranchTemplate`; the tool does not enforce this.
- Bare `dnx Swarm.Squash@0.1.0` (the renderer sample's runbook step) exits 2 and changes nothing; the sample's separate `tool:squash` step is a mismatch for a later renderer change.

## Worktree + epic production plan (2026-10-05)

Plan: [2026-10-03-worktree-epic.md](plans/2026-10-03-worktree-epic.md). Tool reference: [worktree-epic-tools.md](worktree-epic-tools.md). The rows below are as built; where the build differs from the plan's text, the build is recorded.

| Decision | Choice |
|---|---|
| Config | Same `.swarm/batch.json`; new sections `worktree` and `epicTool` only (`epic` is taken by the batch epic id); reuses `worktreeRoot`, `baseBranch`, `stateDir`, `epicBranchTemplate`. |
| Branch naming | Templates with `{id}`, `{slug}`, `{kind}`; `allowedPrefixes` checked at config load and before creation; example-org uses `{kind}/{id}-{slug}` with `feature/`, `bugfix/` only. |
| Managed worktrees | Identified by `branch.<b>.swarm-*` git config, not by path; `<worktreeRoot>/t-<ticket>`. |
| Merged detection | Batch ledger, ancestry, or merge-tree content equality (squash-aware). The ledger is epic-scoped and trusted only when the branch's current tip is no newer than the landing run's start (review ruling C4). |
| Prune safety | Locked never removed; dirty/unmerged/empty only with `--force`; `--dry-run`; per-item failures, exit 4. As built, each removal is a per-worktree `git worktree remove` (no repository-wide `git worktree prune`) and branch deletion is a guarded `update-ref -d <branch> <assessed tip>` plus removal of the `branch.<b>.*` section, not `git branch -D`. |
| Epic close | `--no-ff` merge in a temp worktree, fast-forward the checked-out active branch (`--ff-only --no-overwrite-ignore`) or CAS the ref; refused while any worktree rebases the target; verified before the record is saved; blockers shared with `epic status`; `--force` waives only run-state blockers (`tasks-returned`, `run-unfinished`, `worktrees-unmerged`); `epic close` takes the shared per-epic lock itself and re-assesses under it. |
| Close hooks (review ruling C6, for the human) | The tool-made merge commit uses `--no-verify` and `--no-log`, consistent with `batch`'s integration merges (`--no-verify`): local `commit-msg` and `pre-merge-commit` policies never see epic merge commits. Alternative not chosen: keep the hooks and accept exit 4 for a hook rejection. |
| `--delete-branch` | Guarded `update-ref -d <epic> <merged tip>` plus removal of `branch.<epic>.*`, skipped with a warning when the epic moved or is checked out in any worktree (the plan's `git branch -d` was not used). |
| CLI warnings | `epic close` prints every `warnings` entry as a `warning:` stderr line for every result, before the `blocked`/`conflict` error line; the JSON keeps them in `warnings`. |
| Squash interplay | Own commits are those whose `Epic:` trailer is the epic id or its batch epic id (what the squash lander stamps); `Batch: 0` is shown as manual and runs are counted from `Swarm-Run:`; `squash run` writes no run state, so returned tasks it landed are cleared through the merged check; `batch-running` covers both tools' shared per-epic lock. |
| Versions | `Swarm.Worktree` 0.1.0 and `Swarm.Epic` 0.1.0 (NOT REAL placeholder ids); `Swarm.TestGate` 0.1.2, `Swarm.Batch` 0.2.1 and `Swarm.Squash` 0.1.1 re-packed because their strict config loaders reject the new sections. All five were run through `dnx` from a local feed on 2026-10-05 ([dnx-invocation-notes.md](dnx-invocation-notes.md#worktree-and-epic-010)); nothing is published. |

Review rulings (C1 to C6 of the review ledger, C0 being a process ruling; distinct from the plan's global constraints C1 to C11):

| # | Ruling |
|---|---|
| C1 | Plan B's test fixture `SquashFixture.Worktree(repo)` was renamed `IntegrationFor`, because the new namespace `Swarm.Worktree` hides it (`CS0118`). |
| C2 | No orphan sweep in this plan: after a held-file removal failure git 2.54 has already deregistered the worktree, and a `git worktree prune` run outside the tools deregisters worktrees whose directories are gone (since the final-review fix wave, `batch`, `squash run` and `worktree create` remove only the stale registration of their own path); such task branches and their `swarm-*` metadata are never cleaned up by the tools. |
| C3 | Open, for the human (testgate + batch code): `RunDirectories.Prune` keeps the newest 20 finished runs across all epics, so a busy epic can delete another epic's run evidence behind `tasks-returned`. Fix: prune per epic. |
| C4 | Ledger trust: see "Merged detection" above. |
| C5 | `worktree prune`'s stderr line is `<n> prune item(s) failed (see items[].error in the report on stdout)`; the exit code and stdout are unchanged. |
| C6 | Open, for the human: the close hook policy above. |

Cross-plan facts:

- Production code references no Plan B project; `Swarm.Delivery` re-implements the squash lander's title rule (`MergeMessage.Title`) instead. Plan A and B public surfaces changed only additively: the `worktree` and `epicTool` config sections and `SlotSemaphore.BlocksAcquire` (the lock acquire rule, now shared by `epic status`/`close`); `ILander` and `Program.Run(args, stdout, stderr, cwd, ILander)` are untouched.
- `batch`'s integration merges and `epic close` both bypass hooks with `--no-verify` (C6).
- A crashed local `batch` or `squash run` blocks `epic close` (not waivable) until its lock is older than `expirySec`.
- The cost of a ticket-less task under `squash.requireTicket` for its batch-mates is recorded in [squash-tool.md](squash-tool.md#known-limitations); `epic status` shows such a return's real reason from git's output.
- Stale registrations whose directory still exists are never removed by the tools (`worktree prune` keeps them, also with `--force`, and no tool runs the repository-wide `git worktree prune`); a human inspects them. A `git worktree prune` or `git gc` outside the tools can still deregister them.
- Found by the `dnx` run, fixed in the final-review fix wave: `GitRunner` (Plan A) runs every git call with `-c core.autocrlf=false`, so under Git for Windows' default `core.autocrlf=true` a tracked file git checked out with CRLF read as modified to the tools while `git status` was clean, and `epic close` was blocked by `active-dirty`. `GitRunner.WithRepoLineEndings()` (additive) omits that setting; it is used only where git judges or updates a worktree the user owns: the `active-dirty` check, the `dirty` flag of `worktree list`, `prune`'s non-forced `git worktree remove`, and `epic close`'s fast-forward of the user's checkout. Tool-owned worktrees keep `core.autocrlf=false` ([worktree-epic-tools.md](worktree-epic-tools.md#known-limitations)).
