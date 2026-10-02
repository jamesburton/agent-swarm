# Spike 3A: structured distilled-summary hand-off

> THROWAWAY SPIKE. Run: `dotnet run build-handoff.cs -- ..\scenario handoff-output.md` (from this folder).

| Metric | Value |
|---|---|
| Hand-off size (chars/4) | 1,259 tokens (5,038 chars; target <= 1,500) |
| Build time (incl. `dotnet test`) | 29.9 s (first run 49.8 s, cold build) |
| Tempting wrong fix (`BaseUtcOffset`) included as failed attempt | yes (attempt 3, tagged TEMPTING WRONG FIX) |
| Sections present | Goal, Current state, Files that matter, Failed attempts, Rejected hypotheses, Open question, Constraints, Expected result shape (8/8) |
| LLM calls in builder | 0 (regex over transcript + TRX parse of `dotnet test`) |
| Expert tokens / correct fix / repeated wrong fix | NOT MEASURED (orchestrator runs it, see `run.md`) |

## Files
- `handoff.md` template; `build-handoff.cs` builder; `handoff-output.md` produced artifact; `run.md` expert definition, prompt and evaluation steps; `results.json`.

## Caveats
1. Expert outcome is unmeasured. Only the artifact is verified; verdict is `partial` until the orchestrator runs `run.md`.
2. Determinism is bought by coupling to the transcript format (`## Attempt N: title (STATUS)`, `Idea:`, `Worker notes:`, `Believed facts:`). A free-form worker log would need a distiller (LLM), costing tokens and risking an omitted failed attempt (Review Focus 5).
3. The Rejected hypotheses section restates conclusions from the worker's notes, including its (possibly wrong) belief, labelled unverified. The attempt-3 note ("offset depends on the date being resolved") is a strong hint to the expert that the hand-off does carry from the transcript; that is intended content, but it makes A easier than a hand-off with only bare failure facts.
