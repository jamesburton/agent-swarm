# Variant A run sheet: structured distilled-summary hand-off

Orchestrator-only: the spike agent did NOT run the expert.

## 1. Expert agent definition

Save as `.claude/agents/expert-a.md` (or pass inline):

```markdown
---
name: expert-a
description: Senior debugger. Receives a distilled hand-off from a stuck worker and fixes the bug without repeating failed attempts.
model: opus
tools: Read, Edit, Bash, Grep, Glob
isolation: worktree
maxTurns: 25
---
You are a senior engineer taking over from a stuck, cheaper worker. You get a structured hand-off.
Trust its Failed attempts and Rejected hypotheses as evidence; do not re-try them. Verify the root cause
yourself by reading the code, make the smallest correct fix in library code (never edit or delete tests),
run `dotnet test`, and end with: root cause (one paragraph), diff summary, test summary line.
```

## 2. First prompt (exact)

Build the hand-off with `dotnet run build-handoff.cs -- <scenarioCopy> handoff-output.md`, then send:

```text
You are taking over a bug fix from a worker that got stuck. Your working directory contains the
scenario repo (QuotaKit/, QuotaKit.Tests/). The hand-off below is everything the worker learned.

<handoff>
{contents of handoff-output.md}
</handoff>

Fix the bug so `dotnet test QuotaKit.Tests` passes with all tests. Do not edit tests.
```

## 3. Evaluation steps (orchestrator)

1. Copy `spikes/03-expert-handoff/scenario` to a scratch dir (exclude nothing; the hidden tests stay with the orchestrator, not in the copy). Remove `README.md` and `hidden/` from the copy so the expert cannot see the answer. Run `git init && git add -A && git commit -m base` there so the worktree isolation and a diff exist.
2. Run `build-handoff.cs` against the copy, then spawn the expert via the Agent tool with the definition above (`model: opus`, `isolation: worktree`), prompt from section 2.
3. Record from the Agent result: total tokens / tool uses / duration, and the expert's final message.
4. In the expert's worktree: copy `hidden/AcceptanceTests.cs.txt` to `QuotaKit.Tests/AcceptanceTests.cs`, run `dotnet test QuotaKit.Tests`. Pass = all visible plus hidden tests green.
5. Re-try check: `git diff` of the worktree, and grep the expert transcript for `BaseUtcOffset`, `IsDaylightSavingTime`, `AddHours(1)`, `GetUtcOffset(instant)`. Count as "repeated the known-wrong fix" if any appears in an Edit that was run (mentioning it only to reject it does not count).
6. Record: handoff tokens (about 1,260), expert tokens, correct (hidden tests green), repeated wrong fix (y/n). Compare with the same-model fork baseline.
