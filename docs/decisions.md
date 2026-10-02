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
