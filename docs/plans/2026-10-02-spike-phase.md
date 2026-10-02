---
created: 2026-10-02
updated: 2026-10-02
status: current
---
# Spike Phase Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Note: the spike agents for Tasks 3–6 are dispatched in parallel, one worktree per variant (the spec's approved execution method); the human approved both the spec and this spike scope ("Go, full run").

**Goal:** Build and measure two variants of each of four throwaway spikes (plus a probe set) so the human can choose designs one by one.

**Architecture:** Each spike lives in `spikes/NN-name/{a,b}/` as file-based C# apps (`dotnet run x.cs`) with a `RESULTS.md` and a machine-readable `results.json`. A shared synthetic sandbox generator (Task 2) gives spike 1 a common, repeatable workload. Spike agents commit on their own worktree branch; the orchestrator (this session) merges and writes each spike README.

**Tech Stack:** .NET 10 SDK (10.0.401 for tool probes; file-based apps), git worktrees, PowerShell on Windows 11 (16 logical CPUs), Claude Code agent definitions and `Workflow` scripts.

**Spec:** [docs/specs/2026-10-02-agent-swarm-design.md](../specs/2026-10-02-agent-swarm-design.md) (decisions: [docs/decisions.md](../decisions.md))

## Global Constraints

- All spike code is **throwaway**: every `spikes/NN-name/README.md` starts with `> THROWAWAY SPIKE` and nothing under `spikes/` is referenced from production paths.
- Spike C# runs as file-based apps with `dotnet run <file>.cs` on .NET 10 (`global.json` pins `10.0.401`).
- `.docs/` is git-ignored; run state defaults to `<main-worktree>/.docs/runs/`.
- Docs in `docs/` use relative links, GitHub/ADO-wiki-compatible markdown, no private details, light front-matter (`created`, `updated`).
- Claims are labelled **verified** / **documented** / **unverified** with a source.
- Windows host: PowerShell-native commands; paths short (worktrees under `C:\Development\agent-swarm-wt\`).
- Branches for spike work: `spike/NN<variant>-slug` (this repo has no Azure DevOps prefix rule).
- Commit trailers on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` and the session line.

## Review Focus

1. A spike that "passes" only because the sandbox suite is trivially fast or deterministic — require the generator's delay and a seeded failing test to be configurable and reported in results.
2. Lock directory left behind by a crashed process blocks all later runs — heartbeat expiry must reclaim it (spike 1).
3. Paths over 260 chars or a locked file on Windows break worktree create/prune — spikes must use short worktree roots and report failures instead of hanging.
4. A Markdown definition with a missing/extra section, duplicate role name, or unknown model alias — renderer must fail with a clear message, never emit a partial agent set (spike 2).
5. A hand-off summary that omits the key failed attempt makes the expert repeat it — the seeded bug scenario must contain a tempting wrong fix (spike 3).

---

## File Structure

| Path | Responsibility |
|---|---|
| `global.json` | Pin .NET 10 SDK for spikes |
| `spikes/00-probes/` | Probe scripts + `RESULTS.md` (verification backlog) |
| `spikes/shared/sandbox-gen.cs` | Generate the synthetic .NET repo with a tunable slow suite |
| `spikes/01-batched-tests/{a,b}/` | testgate + batch prototypes per variant |
| `spikes/02-definition-renderer/{a,b}/` | Model/renderer variants + `epic-delivery.md` example |
| `spikes/03-expert-handoff/{a,b}/` | Hand-off formats + seeded-bug scenario |
| `spikes/04-doc-sweeper/{a,b}/` | Lint/sweep variants |
| `spikes/NN-*/README.md` | Question, how to run, results table, verdict (written by orchestrator) |

## Results contract (consumed by the orchestrator and the human review)

Every variant writes `results.json`:

```json
{ "spike": "01", "variant": "a", "question": "…", "metrics": { "name": 1.0 },
  "baseline": { "name": 2.0 }, "verdict": "works|partial|fails", "notes": ["…"], "howToRun": "dotnet run x.cs -- args" }
```

and a `RESULTS.md` (≤60 lines) with the same data as a table plus the three most important caveats.

---

### Task 1: Pin SDK and scaffold spike folders

**Files:**
- Create: `global.json`, `spikes/README.md`

**Interfaces:** Produces the `spikes/` layout and SDK pin used by every later task.

- [ ] **Step 1: Write `global.json`**

```json
{ "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }
```

- [ ] **Step 2: Write `spikes/README.md`**

```markdown
> THROWAWAY SPIKES — not production code. See ../docs/plans/2026-10-02-spike-phase.md.
Run any spike with `dotnet run <file>.cs` from its folder. Results: `RESULTS.md` + `results.json`.
```

- [ ] **Step 3: Verify the pin**

Run: `dotnet --version` from the repo root. Expected: `10.0.401`.

- [ ] **Step 4: Commit** `git add global.json spikes && git commit -m "Pin SDK and scaffold spikes"`

### Task 2: Sandbox generator (shared prerequisite for Task 4)

**Files:** Create `spikes/shared/sandbox-gen.cs`; Test: generated output builds and runs.

**Interfaces:** Produces CLI `dotnet run sandbox-gen.cs -- <outDir> --projects 12 --tests-per 20 --delay-ms 150 --seed 1 --failing 0` which writes a git-initialised solution `<outDir>` with N library projects (`Lib01..`), N test projects (`Lib01.Tests..`, xUnit, each test sleeping `delay-ms`), project references forming a layered graph (`LibK` depends on `Lib(K-1)`), and, when `--failing > 0`, that many seeded tests which fail only when two specific task branches are combined (a cross-task interaction). Also writes `<outDir>/tasks.json`: an array of task descriptors `{ "id": "T001", "touches": ["Lib03/Foo.cs"], "branch": "task/T001" }` and creates those branches each with a trivial, valid edit.

- [ ] **Step 1: Write generator**, with the CLI above.
- [ ] **Step 2: Generate** `dotnet run sandbox-gen.cs -- C:\Development\agent-swarm-wt\sandbox --projects 12 --tests-per 20 --delay-ms 150 --seed 1`.
- [ ] **Step 3: Verify**: `dotnet test` in the sandbox completes; wall time ≈ `projects*tests*delay` ± parallelism; `git branch` lists the task branches.
- [ ] **Step 4: Commit** `git add spikes/shared && git commit -m "Add sandbox generator spike"`

### Task 3: Probe 0 — verification backlog (orchestrator)

**Files:** Create `spikes/00-probes/RESULTS.md`, `spikes/00-probes/*.ps1`.

**Probes (each recorded verified/failed with exact output):**

1. `dnx` help and a tiny package run on SDK 10.0.401 **with and without** `--yes` (prompt behaviour); and on the default 11.0 RC SDK.
2. `dnx` resolves from the Claude Code Bash and PowerShell tools.
3. Per-call `model` override from an Agent tool call (e.g. `haiku`) — confirm the model that actually ran.
4. Fork behaviour: model of a forked agent equals the parent's.
5. `PermissionRequest` hook fires in `claude -p` (test with a no-op logging hook).
6. SDK `AgentDefinition` fields (`hooks`, `isolation`) — read TS/Python reference pages.

- [ ] Steps: run each probe, capture output into `RESULTS.md`, update the **Verification backlog** in `docs/capabilities.md` (strike verified items, relabel claims), commit.

### Task 4: Spike 1 — batched tests + bisect (two Sonnet agents)

**Files:** `spikes/01-batched-tests/a/`, `spikes/01-batched-tests/b/` (each: `testgate.cs`, `batch.cs`, `simulate.cs`, `RESULTS.md`, `results.json`).

**Interfaces:**
- Consumes: sandbox from Task 2 and its `tasks.json`.
- Produces per variant: `testgate.cs -- run [--slots N] -- <cmd…>` (acquire/release a lock-directory slot under `<main>/.docs/runs/slots/`, heartbeat file, expiry 60 s, print `{"waitMs":…,"runMs":…}`), `batch.cs -- <sandbox> <tasks.json> [--min 2 --max 8]` (sequential merge into an integration worktree, stop-on-conflict, full suite via testgate, halving bisect on red, squash-at-green onto `epic/E1`), `simulate.cs` (runs baselines + batched modes, writes `results.json`).
- Variant A: fixed batch size N with halving bisect. Variant B: adaptive size (×2 after green, ÷2 after red, within min/max) plus file-overlap pre-batching using `touches`.
- Metrics (must be in `results.json`): serial wall time (full suite per task), batched wall time, number of full-suite runs, CPU-slot utilisation, culprit isolation correctness (with `--failing 1` and `--failing 2`), crashed-lock recovery demonstrated.

- [ ] Steps: agent builds, runs `simulate.cs` for failing ∈ {0,1,2}, writes results, commits on its branch.

### Task 5: Spike 2 — definition → agents/workflow (two Sonnet agents)

**Files:** `spikes/02-definition-renderer/{a,b}/` + shared input `spikes/02-definition-renderer/epic-delivery.md` (the Markdown example from the design Q&A, with roles orchestrator(code), worker, expert, reviewer, gate batch-green).

**Interfaces:** each variant exposes `dotnet run render.cs -- <definition.md> <outDir>` writing `<outDir>/.claude/agents/<role>.md` per LLM role and `<outDir>/.claude/workflows/<name>.js` for the code orchestrator; exit code ≠ 0 with one-line message on invalid input (unknown model alias, duplicate role, missing required key), emitting **no** files in that case.
- Variant A: typed canonical model (`record Swarm(Role[] Roles, Gate[] Gates, string[] Flow)`) → renderers; also a stub YAML front-end proving a second format maps to the same model. Variant B: thin pass-through (each role section → agent file) + workflow template.
- Metrics: example renders; generated agent files parse (front-matter valid, fields in the supported list); workflow script passes `node --check`; invalid-input cases (Review Focus 4) rejected cleanly; lines of code and effort to add a second format.

- [ ] Steps: agent builds, runs the four invalid-input cases, writes results, commits.

### Task 6: Spike 3 — worker → expert hand-off (two Sonnet agents)

**Files:** `spikes/03-expert-handoff/{a,b}/` + shared `scenario/` (small C# repo with a seeded bug whose obvious fix is wrong; a recorded "worker transcript" of failed attempts).

**Interfaces:** each variant provides `handoff.md` (the template), `build-handoff.cs -- <scenario> <out>` producing the hand-off artifact, and `run.md` (the exact prompt and agent definition for the expert).
- Variant A: structured distilled summary (goal, state, files, failed attempts with why, open question). Variant B: pointer-based (notes file in the worktree + one-paragraph summary; expert reads files itself).
- Metrics: hand-off size (tokens approx = chars/4), expert tokens to solution (from the run's usage), correct fix achieved (scenario tests pass), repeated the known-wrong fix (Review Focus 5), vs a same-model fork baseline.

- [ ] Steps: agent builds both artifacts, the **orchestrator** runs each on an `opus` expert (needs the Agent tool; spike agents may not nest), records results.

### Task 7: Spike 4 — doc-lifecycle sweeper (two Sonnet agents)

**Files:** `spikes/04-doc-sweeper/{a,b}/` (`sweep.cs`, `RESULTS.md`, `results.json`) + `docs/` as the test corpus.

**Interfaces:** `dotnet run sweep.cs -- <docsDir> [--today 2026-10-02] [--json]` reports per file: missing/invalid `created`/`updated`, age, proposed status (`current|stale?|archive`), index-drift (files not linked from `AGENTS.md`/README), broken relative links; exit code 1 if any error.
- Variant A: deterministic only; per-type age thresholds from a table in the file header (design decision 90 d, device fact 180 d, benchmark number 30 d, default 90 d). Variant B: A plus, for stale candidates, a `claude -p --model haiku` relevance check against the repo, returning keep/refine/archive with a one-line reason.
- Metrics: findings on this repo's docs, runtime, false-positive review (human spot-check list), threshold proposals.

- [ ] Steps: agent builds, runs against `docs/`, writes results, commits.

### Task 8: Integrate and prepare the one-by-one review

- [ ] Merge each variant branch into `main` (sequential, stop-on-conflict), run each `RESULTS.md` check, write `spikes/NN-name/README.md` (question, how to run, results table, A vs B, recommendation).
- [ ] Update `docs/capabilities.md` backlog from Task 3 and add run-cost notes (tokens/time per agent) to `docs/decisions.md`.
- [ ] Present spikes **one at a time** with an options choice (adopt A / adopt B / combine / rework / drop); log each verdict in `docs/decisions.md`.

## Self-review

- **Spec coverage:** spikes 0–4 map to Tasks 3–7; review format to Task 8; architecture/packaging/non-goals are deliberately out of this plan (post-spike plans). Gap: none for the approved scope.
- **Placeholders:** none; spike agents choose internals but not interfaces or metrics.
- **Type consistency:** `results.json` contract and `tasks.json` descriptor are used identically in Tasks 2, 4–7.
