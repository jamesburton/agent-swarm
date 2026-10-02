---
created: 2026-10-02
updated: 2026-10-02
status: draft-for-review
---
# agent-swarm design

Consolidates [decisions.md](../decisions.md). Background: [capabilities](../capabilities.md), [workflow](../workflow.md), [doc lifecycle](../doc-lifecycle.md), [landscape](../landscape.md).

## 1. Purpose and success

Paste a swarm definition into a session and run it as a wide, cheap, orchestrated fleet: small-model workers, experts on demand, batched integration tests, ticket-sized squashed commits per epic, clean `--no-ff` landing. Success = the same epic completes with materially fewer tokens and less wall-clock than a serial, single-agent, per-ticket-full-suite baseline, with a history readable by `--first-parent`.

## 2. Architecture

```
definition (md | yaml | C#)  ──parse──▶  canonical model  ──render──▶  agent files + Workflow script
                                                │                              │
                                                └── later: swarm run (interpreter) ──▶ Claude Code sub-agents
run state (.docs/runs | shared dir):  queue · batch results · slot locks · gate evidence
tools (dnx):  worktree · testgate · batch · squash · epic · swarm · docs-sweep
plugin (text): skills · agent templates · workflow templates · hooks
```

- **Canonical model:** roles (model, tools, skills, isolation, effort, max turns, escalate-to, orchestrator kind `code`/`llm`), gates, flow. Three front-ends (Markdown reference, YAML, C#) produce the same model.
- **Outputs:** `.claude/agents/*.md` + `.claude/workflows/*.js` first; `swarm run` interpreter follows.
- **Elevation:** expert = fresh agent on a stronger model receiving a distilled context (forks cannot change model).
- **Sign-off:** declared gates map to permission policy, `PermissionRequest` hooks, machine-evidence checks (code reads testgate output), or human via RC/channel. Agent messages never authorise actions.
- **Run state:** default `<main-worktree>/.docs/runs/` (git-ignored); configurable to a shared directory. All worktrees resolve the main worktree via `git rev-parse --git-common-dir`.

## 3. Delivery mechanics

- Workers: one worktree from the epic branch, targeted tests chosen by project-graph affected analysis, fixed-shape result.
- Batch: sequential merge into an integration worktree, stop-on-conflict (offender returns to its worker). Full suite once per batch under a CPU slot (lock directory, atomic create, heartbeat expiry).
- Batch size adaptive within min/max; red batch bisected by halving; greens land as they pass.
- Squash at batch-green: one trailer-stamped commit per ticket (`Ticket:`, `Epic:`, `Batch:`) onto `epic/<id>-<slug>` (or `feature/`/`bugfix/` per repo config); epic merged `--no-ff` to the active branch.

## 4. Docs lifecycle

Light front-matter (`created`, `updated`, optional `reviewed`, `status`), `[STALE?]` marking, archive when obsolete, on-read check + scheduled sweep + manual `dnx` lint. Published as an Azure DevOps code wiki from `docs/`.

## 5. Packaging

Claude Code plugin (skills, four starter roles: worker, orchestrator/sub-orchestrator, expert, reviewer+test-triage, templates, workflows) + independently versioned `dnx` tools. .NET 10+ is the only stated requirement.

## 6. Spike plan

Built under `spikes/NN-name/` on `main`, each labelled throwaway with a README (question, how to run, results, verdict). One worktree + one Sonnet agent per variant; I integrate. Reviewed one by one with options.

| # | Question | Variant A | Variant B | Evidence |
|---|---|---|---|---|
| 0 | Probes from the [verification backlog](../capabilities.md#verification-backlog) | n/a | n/a | `dnx --yes` on this SDK; `dnx` from Claude Code's shell; per-call `model` override; fork model; `PermissionRequest` in `-p` |
| 1 | How much does batching + bisect save? | Fixed-N halving bisect, lock-dir testgate | Adaptive size + file-overlap pre-batching | Wall time, full-suite runs, slot utilisation vs serial baseline, on a generated sandbox repo with a tunable slow suite |
| 2 | Best definition → agents/workflow path? | Typed canonical model → renderers | Thin Markdown pass-through (section → agent file) + workflow template | Epic-delivery example renders; agent files load; workflow dry-runs; effort to add YAML/C# front-ends |
| 3 | Best worker → expert hand-off? | Structured distilled summary into fresh expert | Pointer-based (notes file + worktree), minimal summary | Tokens, success on a seeded hard bug, vs a plain same-model fork baseline |
| 4 | Doc sweep thresholds and method? | Deterministic front-matter lint + age report | A + haiku relevance check on candidates | Findings on this repo's docs, false-positive rate, proposed per-type age thresholds |

Third options (octopus merge, time-window batches, etc.) are written up, not built.

## 7. Review format

Per spike, one at a time: card with question, how to run, results table, A vs B evidence, recommendation; then an options choice (adopt A / adopt B / combine / rework / drop). Approved choices become entries in [decisions.md](../decisions.md) and seed the real tools.

## 8. Non-goals (for now)

Codex/Gemini renderers, the `swarm run` interpreter, non-.NET affected-test analysis, GitHub wiki sync, multi-machine (Strix/T5500) runs.

## 9. Risks

- Three definition front-ends in parallel may cost more than expected → fall back to staging (Markdown first).
- Workflow tool needs explicit opt-in and has 16/1,000 limits → keep an LLM-orchestrator path.
- Windows locking/worktree path-length quirks.
- Several capability claims are still unverified until spike 0.
