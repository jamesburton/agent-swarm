> THROWAWAY SPIKE 4B: deterministic sweeper + `claude -p --model haiku` check

Run from this folder: `dotnet run sweep.cs -- ../../../docs [--today 2027-03-01] [--json] [--llm]`; `dotnet run sweep.cs -- --selftest` (13 checks, pass).

| Metric | Value |
|---|---|
| Docs scanned (repo `docs/`) | 7 |
| Errors / warnings at `--today 2026-10-02` | 10 / 3 (exit 1) |
| Docs missing front-matter | 5 of 7 (capabilities, decisions, doc-lifecycle, landscape, workflow) |
| Index drift (not linked from AGENTS.md/README.md) | 3 (decisions, plan, spec) |
| Broken relative links | 0 |
| Deterministic runtime | 193 ms (3.6 s wall incl. file-app compile) |
| Stale candidates at `--today 2027-03-01` | 2 (plan, spec; both 150 d) |
| `--llm` run | 2 calls (cap 8), 171 s total, ~85 s/call |
| Haiku verdicts | plan: keep; spec: refine |
| claude missing | verdict `unavailable`, run continues (verified via PATH removal) |

## Verdicts to review by hand
- `docs/plans/2026-10-02-spike-phase.md` keep: "created today" - model used real date, ignored forced `--today`. Right in reality, uninformative for the test.
- `docs/specs/2026-10-02-agent-swarm-design.md` refine: "spike execution status unclear; needs results update". Plausible (spikes are mid-flight).
- Nothing looked clearly wrong; sample too small (2) to judge precision.

## False-positive notes
- Docs without front-matter produce hard errors and `unknown` status; they are the majority here, so the LLM path hardly triggers until front-matter is added.
- Index drift counts only direct links from AGENTS.md/README.md; decisions/plan/spec are linked indirectly via docs/ pages.
- Type is guessed from filename (`spec|decision|design`=decision, `capabilit|device|probe`=device, `benchmark|results|perf`=benchmark); override with front-matter `type:`.

## Proposed thresholds
Decision 90 d, device fact 180 d, benchmark 30 d, default 90 d; archive at 2x. Age uses max(`updated`, `reviewed`).

## Caveats
1. LLM check is slow (~85 s/call here) and cannot be given a fake "today"; use it only for real stale candidates, capped at 8 per run.
2. Prompt gets doc (12k chars max), first 60 tracked files and `git log -5` for existing paths mentioned; no verification the model inspected anything beyond that.
3. Cap logic (8) is implemented but not exercised (only 2 candidates in this repo).
