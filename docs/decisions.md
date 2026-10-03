---
created: 2026-10-02
updated: 2026-10-02
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
