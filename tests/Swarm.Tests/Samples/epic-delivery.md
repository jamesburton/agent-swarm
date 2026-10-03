---
name: epic-delivery
description: Deliver an epic with cheap workers, on-demand experts and a batched test gate.
---
# Epic delivery swarm

## orchestrator  (code)
flow: worker*, gate:batch-green, reviewer, tool:squash

## worker  (llm)
model: haiku
description: Implements exactly one task in its own worktree and returns a fixed-shape result.
tools: Read, Edit, Write, Grep, Glob, Bash
maxTurns: 30
effort: low
isolation: worktree
escalate-to: expert
You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes.

## expert  (llm)
model: opus
description: Solves what a worker could not, from a distilled hand-off.
tools: Read, Edit, Grep, Glob, Bash
maxTurns: 40
effort: high
context: distilled
You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts.

## reviewer  (llm)
model: sonnet
description: Reviews a green batch diff for correctness and style.
tools: Read, Grep, Glob
maxTurns: 15
effort: medium
Review the diff. Report blocking issues first.

## tool: squash
package: Swarm.Squash
version: 0.1.0

## gate: batch-green
kind: test
tool: squash
