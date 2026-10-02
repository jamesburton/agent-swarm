> THROWAWAY SPIKE. Variant B (pointer-based) run sheet for the orchestrator.

## 1. Expert agent definition (`.claude/agents/escalation-expert.md`)

```markdown
---
name: escalation-expert
description: Escalation expert that fixes a problem a cheaper worker got stuck on, using the worker's .handoff/ notes.
model: opus
tools: Read, Edit, Bash, Grep, Glob
isolation: worktree
maxTurns: 25
---
You are the escalation expert. A cheaper worker was stuck; its state is in `.handoff/` in your working directory. Read `.handoff/NOTES.md` first, follow its evidence pointers as needed, never repeat a listed failed attempt, never delete or weaken tests, and finish with a short report: root cause, the change made, and the final `dotnet test` result.
```

## 2. Exact first prompt (contents of `handoff-output/handoff-prompt.md`)

```text
You are the escalation expert. A cheaper worker got stuck on this task: "`dotnet test` in `QuotaKit.Tests` has 2 failing tests; fix the library so all tests pass; do not delete tests." Repo root: the current directory; run the tests with `dotnet test QuotaKit.Tests`. The worker's state is in `.handoff/`: read `.handoff/NOTES.md` first (running notes; every failed attempt is one line with a pointer to evidence), then open whatever evidence it points to (`last-test-run.txt`, `attempts.diff`, `worker-transcript.md`). Do not repeat a failed attempt listed there. Fix the library (never delete or weaken tests), make all tests pass, and reply with the root cause and the change you made.
```

## 3. Orchestrator evaluation

1. Build: `cd spikes/03-expert-handoff/b; dotnet run build-handoff.cs -- ../scenario handoff-output` (re-run to regenerate; deterministic).
2. Scratch dir (short path, inside a dir that sees the repo `global.json`, or copy it): copy ONLY `scenario/QuotaKit/` and `scenario/QuotaKit.Tests/` (no `README.md`, no `hidden/`, no `worker-transcript.md` at the root) to `<scratch>/`; copy `handoff-output/.handoff/` to `<scratch>/.handoff/`; `git init` + commit so the worktree isolation has a baseline.
3. Spawn the expert (Agent tool, `subagent_type: escalation-expert`, `model: opus`) with the exact prompt from section 2, working in `<scratch>`.
4. Record the expert's reported token usage (total tokens and tool-use count) and wall time.
5. Grade: copy the hidden acceptance tests into the expert's resulting worktree `QuotaKit.Tests/`, run `dotnet test QuotaKit.Tests`; correct fix = all visible and hidden tests green with no tests deleted/weakened (`git diff -- QuotaKit.Tests` shows only additions).
6. Repeat-of-wrong-fix check: scan the expert's transcript/tool calls and diff for `BaseUtcOffset` (the tempting fix, attempt 3) and for `IsDaylightSavingTime` (attempt 2) being edited into `DayBoundary.cs` or run through `dotnet test`. Any such edit = the known-wrong fix was re-tried; note whether it read NOTES.md first (a Read call on `.handoff/NOTES.md`).
7. Baseline comparison: same task by a same-model fork (full-context) and by an opus agent with no hand-off; compare tokens-to-solution.
8. Fill `b/results.json` `metrics`: `expertTokensToSolution`, `correctFix` (bool), `repeatedWrongFix` (bool), `notesRead` (bool); set `verdict`.
