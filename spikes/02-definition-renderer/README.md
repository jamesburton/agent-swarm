> THROWAWAY SPIKE — swarm definition → agent files + Workflow script. Run 2026-10-02.

**Question:** typed canonical model with renderers (A) or thin Markdown pass-through (B)? Each variant has its own example dialect (`a|b/epic-delivery.md`); run `dotnet run render.cs -- epic-delivery.md <outDir>`.

| | A typed model | B pass-through |
|---|---|---|
| Size | 322 lines | 115 lines |
| Example renders (3 agents + 1 workflow, `node --check` ok) | yes | yes |
| Invalid inputs rejected, 1 line, no files (bad model, duplicate role, missing model, missing section, extra section) | 5/5 | 5/5 |
| Second front-end | stub YAML ~40 lines, output identical to Markdown | est. ~40 lines for YAML (not built); C# needs a typed shape anyway |
| Field validation | validated by model | only `model` checked; `maxTurns: abc` passes; `context`/`escalate-to` dropped |

Caveats: generated workflows were syntax-checked only, never run in the Workflow tool; A's YAML is a hand-rolled subset; gate evidence not wired in either.

**Verdict:** both work. B is 3x smaller; A is the only one that scales to the three-format decision and validates fields. Recommend A, borrowing B's all-output-in-memory-then-write approach (A also does this).

---

## Workflow dry run (2026-10-03, real `Workflow` tool; opted in by the human)

Script: variant A's generated `epic-delivery.js`, with patches (below). Run 1 (2 tasks): worker x2 -> gate -> reviewer -> squash = 5 agents, 164k tokens, 23 s, all returned valid structured output. Run 2 (1 task reporting `blocked`): worker -> expert escalation fired -> gate -> reviewer -> squash = 5 agents, 177k tokens, 27 s.

**What works:** `pipeline` fan-out, per-call `model`, `schema` results, escalation branch (`blocked` -> expert agent receiving the hand-off), phase grouping, sequential gate/reviewer/tool phases.

**Bugs and gaps found (renderer fixes needed):**
1. **Mixed line endings:** generated script has CR chars (25 CR / 31 LF); the Workflow tool refuses a script with control characters. Normalise output to LF.
2. **`--yes` emitted for `dnx`:** not a flag on SDK 10.0.401 or 11.0 RC (probe 0). Remove.
3. **Bare package ids** (`dnx testgate`, `dnx squash`) would fetch arbitrary NuGet packages by those names (supply-chain risk). Definitions must give explicit package id + pinned version; renderer must refuse bare placeholders. Both commands were patched to `echo` for the run and never executed.
4. **Custom `agentType` not found:** `worker`/`expert`/`reviewer` agent files installed mid-session were not in the registry ("Available agents: claude, ..., general-purpose, ..."), so the generated script fails unless agent files exist at session start. Dry run substituted `general-purpose`. Reload behaviour (new session vs. `/agents`) unverified.
5. **Blocked tasks flow on:** after a failed escalation the script still ran gate, reviewer and squash on a `blocked` result. Filter on `status === 'done'`, and surface unresolved tasks in the return value.
6. **Gate/tool steps cost an LLM call:** a Workflow script cannot exec commands, so `dnx` gate/squash steps ran as haiku agents (~31k tokens each for an `echo`), contradicting the zero-orchestrator-token goal. Deterministic tool steps (testgate, batch, squash) belong outside the workflow (main-session Bash or hooks); the workflow should do only LLM fan-out and read gate evidence.
7. **Reviewer prompt thin:** it receives only `Process: <json>` and no diff reference; the role needs a defined input (branch/commit range).
8. **Per-agent overhead ~31-35k tokens** even for a no-tool task; a swarm's floor cost is agents x ~33k, so fan-out width should be justified by work, not by default.

Patches applied to the dry-run copy only: `echo` replaces the two `dnx` commands; custom `agentType`s -> `general-purpose`; CRs stripped. Temporary agent files were removed afterwards.
