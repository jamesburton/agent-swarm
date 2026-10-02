---
created: 2026-10-02
updated: 2026-10-02
status: current
---
# Delivery workflow: agents, tests, batches, epics

Design for tools and templates that spool out small agents, coordinate expensive tests, and land work as clean history. **Proposed, not built.** Capability constraints come from [capabilities.md](capabilities.md).

## 1. Agent hierarchy

```
Epic orchestrator            (expert model, or a Workflow script)
 └─ Sub-orchestrator / ticket (small model, one per ticket or cluster)
     └─ Worker                (small model, one task, one worktree)
         └─ Expert (on demand; fresh agent on a stronger model + distilled context)
```

- Depth is 3 by default (`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`), which fits orchestrator → sub-orchestrator → worker. Experts are summoned by the sub-orchestrator, not nested deeper.
- **Orchestration as code where possible.** A `Workflow` script (`pipeline`/`parallel`, per-stage model) coordinates deterministically at zero orchestrator-token cost; LLM orchestrators are used only where judgement is needed. Needs explicit opt-in per run.
- Orchestrators stay lean: they read manifests and result summaries, never source. Workers return a fixed-shape result (status, branch, commit, test summary, ≤10-line notes).
- Templates generate `.claude/agents/*.md` (or per-call overrides) from one swarm definition: role, model, tools, skills, `maxTurns`, effort, worktree isolation.

## 2. Branch model

| Level | Branch | Notes |
|---|---|---|
| Active | `main` / `dev` | Receives **non-squashed merges** only |
| Epic | `epic/<id>-<slug>` | Integration line for one epic; ticket commits are squashed onto it |
| Ticket / task | `task/<ticket>-<slug>` | One worktree per worker, disposable |

- Where a repo's CI needs it (e.g. `example-org` Azure DevOps), the epic and ticket branches must use the full `feature/…` / `bugfix/…` prefixes from the global instructions; the naming scheme is configurable per repo.
- Built-in `isolation: worktree` branches from the **default branch**, not an epic branch, so the tooling creates worktrees itself (`git worktree add -b task/… <path> <epic-branch>`).

## 3. Test coordination

Goal: never serialise expensive full suites per task.

1. **Worker-local:** run only the targeted tests (affected projects/files) in its own worktree. Cheap, parallel.
2. **Batch gate:** finished task branches queue for integration. A batch of N is merged into a throwaway integration worktree and the **full suite runs once**.
3. **Green →** each ticket is squashed to one commit onto the epic branch (ticket/solution-sized, in queue order).
4. **Red →** bisect: split the batch in halves and re-run; failing task(s) go back to their worker with the failure output. Cost is ~log₂N full runs instead of N.
5. **CPU slots:** a test gate (counting semaphore sized to cores/memory, shared across worktrees and sessions via a lock directory) limits concurrent heavy runs so wide agent fan-out cannot starve the machine. Tests are the only metered resource; agent count is not.

## 4. Landing on the active branch

- Epic branch → active branch via `git merge --no-ff`, **never squashed**, so the active branch gets one merge chunk per epic (or per batch for large epics) and every squashed ticket commit stays inside it.
- Read history with `git log --first-parent` for chunk-level view, plain `git log` for ticket-level.
- Commit trailers on every squashed commit: `Ticket:`, `Epic:`, `Batch:`, plus the agent run id for traceability.

## 5. Tools (`dnx`-run, .NET 10+)

Package IDs to be decided; each is a single-purpose CLI (optionally also an MCP server via `dnx` in `.mcp.json`):

| Tool | Does |
|---|---|
| worktree | create/list/prune per-task worktrees from an epic branch |
| testgate | acquire a CPU/test slot, run a command, release; report timing |
| batch | build an integration worktree from queued branches, run the gate, bisect on failure |
| squash | squash a ticket branch to one trailer-stamped commit onto the epic branch |
| epic | open/close an epic: create the branch, merge `--no-ff` to the active branch |
| swarm | render a swarm definition into agent files / a Workflow script |

Requirement stated once in the README: .NET 10+. No other install steps.

## 6. Open questions

1. Squash happens at batch-green time (proposed) vs. at worker completion; the former keeps rework off the epic branch.
2. Octopus merge vs. sequential merge into the integration worktree (conflict attribution).
3. Where run state lives (a git-ignored `.docs/` file vs. a shared directory visible to peer sessions).
4. Slot-lock across Windows sessions: lock directory vs. named mutex.
5. How large an epic may grow before it is split into several `--no-ff` chunks.
