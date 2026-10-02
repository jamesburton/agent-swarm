> THROWAWAY SPIKE 4A: deterministic doc-lifecycle sweeper

Run (from this folder): `dotnet run sweep.cs -- ../../../docs --today 2026-10-02 [--json]`; self-check: `dotnet run sweep.cs -- --selftest`.
Verdict: **works** (deterministic; selftest 15/15 pass).

## Findings on this repo's `docs/` (7 files, today = 2026-10-02)

| Kind | Count | Notes |
|---|---|---|
| no-front-matter / missing-created / missing-updated | 5 / 5 / 5 | capabilities, decisions, doc-lifecycle, landscape, workflow (errors, not edited) |
| invalid-created / invalid-updated | 0 / 0 | |
| broken-link | 0 | |
| index-drift | 3 | decisions, plans/spike-phase, specs/design |
| invalid-status | 2 | `executing`, `draft-for-review` are outside the convention's value set |
| stale? / archive | 0 / 0 | everything is 0 days old |
| Exit code | 1 | 10 errors |

Runtime: ~0.6-1.1 s in-process (cold JIT); `dotnet run` build overhead dominates wall time.
Selftest cases (testdata/): valid, missing date, bad date, stale by age (stale?), old benchmark (archive), broken link (code/fenced links ignored), orphan.

## Proposed thresholds (days since max(updated, reviewed); stale? at T, archive at 2T)

| Type | T | Archive | Reasoning |
|---|---|---|---|
| design decision | 90 | 180 | decisions are revisited when context shifts; a quarter is a sensible re-check |
| device fact | 180 | 360 | hardware facts change slowly; firmware/driver notes may need sooner review |
| benchmark number | 30 | 60 | depends on model/tool versions that move monthly |
| default | 90 | 180 | |

## Caveats

1. Type inference is keyword-based on path (`decision|design|spec|plan`, `device|hardware`, `benchmark|perf|results`) unless `type:` is set; mislabels are likely, so docs should set `type:` explicitly.
2. Age is calendar-based only: a doc that is old but still true is a false positive (cure: bump `reviewed`); a recently edited but wrong doc is a false negative. No relevance check (that is variant B).
3. Index drift counts only direct links from `../AGENTS.md` / `../README.md`; docs reached via another doc are reported as drift. Anchors are not validated and nothing is checked in external URLs.
