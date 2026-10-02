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
