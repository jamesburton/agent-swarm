# epic-delivery

## orchestrator  (code)

stages: worker, batch-green, reviewer

## worker  (haiku)

description: Implements one ticket in its own worktree and runs targeted tests only.
tools: Read, Edit, Write, Bash, Grep, Glob
isolation: worktree
maxTurns: 40
escalate-to: expert

Implement the ticket you are given. Run only the targeted tests. Return a fixed-shape
result: status, branch, commit, test summary, at most 10 lines of notes.

## expert  (opus)

description: Fresh, stronger-model agent summoned when a worker is stuck.
tools: Read, Edit, Bash, Grep, Glob
effort: high
context: distilled

You receive a distilled hand-off (goal, state, files, failed attempts with reasons, open
question). Do not repeat the listed failed attempts. Fix the problem and report the diff.

## reviewer  (sonnet)

description: Reviews a squashed ticket commit against the ticket and the repo conventions.
tools: Read, Grep, Glob, Bash
disallowedTools: Edit, Write
effort: medium

Review the commit for correctness and convention. Reply approve or request-changes with reasons.

## gate batch-green

kind: test
command: dnx testgate -- dotnet test
