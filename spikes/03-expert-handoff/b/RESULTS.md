> THROWAWAY SPIKE. Spike 3, variant B: pointer-based hand-off.

**Question:** can a <=150-word paragraph plus a `.handoff/` folder the expert reads itself replace a long distilled summary?

| Metric | Value |
|---|---|
| Prompt paragraph | 689 chars, 107 words, ~172 tokens |
| `.handoff/` folder total | ~2662 tokens (chars/4) |
| - NOTES.md | ~488 tokens |
| - last-test-run.txt | ~676 tokens |
| - attempts.diff | ~275 tokens |
| - worker-transcript.md | ~1222 tokens |
| Build time (incl. `dotnet test`) | 34.3 s (first run 52.6 s, restore) |
| Tempting fix (BaseUtcOffset, attempt 3) in NOTES.md as failed | yes, tagged `[TEMPTING WRONG FIX]` with why-it-failed and line pointer |
| Expert tokens / correct fix / repeated wrong fix | not run (orchestrator, see `run.md`) |

## Files
- `handoff.md` template + folder contract; `build-handoff.cs` deterministic builder (no LLM); `run.md` agent definition, exact prompt, evaluation steps.
- `handoff-output/` is the real output of one build.

## Caveats
1. Only the paragraph (~172 tokens) is pushed to the expert; the folder (~2.5k tokens) is pulled on demand, so the expert's real cost depends on how much it reads (measure in the run).
2. NOTES.md one-liners are derived mechanically from the transcript ("Idea:" and first failure-keyword sentence of "Worker notes:"); a real worker must keep them in that shape, and quality depends on its discipline.
3. `attempts.diff` is reconstructed from transcript code blocks (attempt 4 had none); `last-test-run.txt` contains absolute temp-copy paths from the throwaway build dir.
4. The transcript itself contains the worker's hint ("depends on the date being resolved"); the expert may find the answer there, which is a feature of pointer-based hand-off but inflates the fix rate vs. a summary that omits it.
