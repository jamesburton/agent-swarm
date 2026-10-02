> THROWAWAY SPIKE — doc-lifecycle sweeper. Run 2026-10-02.

**Question:** deterministic front-matter/age/index/link sweep (A) or A plus a Haiku relevance check on stale candidates (B)?

Run: `dotnet run sweep.cs -- ../../docs [--today D] [--json]` (B adds `--llm`; `--selftest` in both).

| | A deterministic | B + `--llm` |
|---|---|---|
| Selftest | 15/15 | 13/13 |
| Findings on `docs/` (7 files) | 10 errors: 5 missing front-matter, 3 index drift, 2 non-standard status | identical |
| Runtime | 0.6-1.1 s | 193 ms in-process; `--llm` ~85 s per Haiku call under load |
| LLM verdicts (forced `--today 2027-03-01`) | n/a | 2 calls: plan keep, spec refine; neither clearly wrong; Haiku used the real date, ignoring the forced one |
| `claude` missing | n/a | degrades to "unavailable" |

Proposed thresholds (both): decision 90 d, device fact 180 d, benchmark 30 d, default 90 d, archive at 2x.

Caveats: type guessed from filename; age cannot tell stale from still-correct; drift counts only direct links; docs without front-matter get "unknown" and never reach the LLM; 8-call cap untested; 2-doc LLM sample.

**Verdict:** adopt A as the lint/CI core (fast, deterministic). B's LLM check is a plausible optional review step, but is slow and unproven on 2 samples. Note this repo's own docs fail the sweep (no front-matter): fixing them is a real to-do.
