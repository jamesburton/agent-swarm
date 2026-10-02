> THROWAWAY SPIKE. Variant B: pointer-based hand-off template.

# Hand-off template (pointer-based)

The expert receives ONE paragraph (<= 150 words) and reads everything else itself from `.handoff/`.

## The paragraph (placeholders in braces)

> You are the escalation expert. A cheaper worker got stuck on this task: "{task}". Repo root: `{repoRoot}`; run the tests with `{testCommand}`. The worker's state is in `.handoff/`: read `.handoff/NOTES.md` first (running notes; every failed attempt is one line with a pointer to evidence), then open whatever evidence it points to. Do not repeat a failed attempt listed there. Fix the library (never delete or weaken tests), make all tests pass, and reply with the root cause and the change you made.

## `.handoff/` folder contract

| File | Content | Written by |
|---|---|---|
| `NOTES.md` | Running worker notes: status, believed facts, open question, and one line per failed attempt: `- Attempt N (title): what was tried. Failed: why. Evidence: worker-transcript.md:Lx-Ly` | worker (here: build-handoff.cs) |
| `last-test-run.txt` | Raw, unedited output of the last `dotnet test` | worker / build script |
| `attempts.diff` | Cumulative diff of everything tried (all reverted) so the expert can see exact code | worker / build script |
| `worker-transcript.md` | Full transcript (optional evidence target for NOTES pointers) | worker |

Rules: notes lines stay one line each; evidence is by pointer, never pasted into the paragraph; the paragraph never contains a diagnosis.
