# agent-swarm

Pasteable **swarm definitions** that run as wide, orchestrated fleets of Claude Code agents — cheap/small models for the bulk of the work, expert (elevated-model) agents only where needed.

> Status: **early tooling**. The capability study and spikes are done, and the first tool is built: `swarm`, which validates a swarm definition (Markdown, YAML or C#) and renders Claude Code agent files, workflow scripts and a runbook. It is not published yet and has not run end to end with real agents. Five more tools are built but not published: `testgate`, `batch`, `squash`, `worktree` and `epic` (see [docs/batch-tools.md](docs/batch-tools.md), [docs/squash-tool.md](docs/squash-tool.md), [docs/worktree-epic-tools.md](docs/worktree-epic-tools.md) and the plans [testgate + batch](docs/plans/2026-10-03-testgate-batch.md), [squash](docs/plans/2026-10-03-squash.md) and [worktree + epic](docs/plans/2026-10-03-worktree-epic.md)). See [docs/definition-format.md](docs/definition-format.md).

## Goals

1. **Define swarms as text.** A swarm (roles, models, tools, instructions, hand-offs) is a document you can paste into a session and run.
2. **Optimise tokens and speed.** Small models (e.g. Haiku/Sonnet) with tight, well-written instructions do the parallel fan-out; experts are summoned on demand.
3. **Elevate on demand.** Clone any worker into an expert variant on a stronger model — forked context by default, shared context as an opt-in.
4. **Trusted sign-off.** Understand and use what is (and is not) possible for approval/hand-off between sub-agents, other local sessions, and Remote Control (RC) / cloud sessions.
5. **Lean orchestration and clean history.** Small agents under orchestrators/sub-orchestrators; worktree-isolated tasks, batched integration testing (one full-suite run per batch, bisect on failure), ticket-sized squashed commits per epic branch, merged `--no-ff` to the active branch. See [docs/workflow.md](docs/workflow.md).
6. **Clean tooling.** Tools are launched with minimal ceremony, e.g. `dnx <our-nuget-id>`, so the only setup requirement is a link to .NET 10+.

## Scope of the first pass

- Capability matrix: what we can and can't refine about sub-agents; what we can and can't do with cross-session communication and sign-off. See [docs/capabilities.md](docs/capabilities.md).
- Overview of recent work (last 6 months, i.e. since 2026-04) in the parent development folder that this builds on. See [docs/landscape.md](docs/landscape.md).
- A first swarm-definition format and a worked example: [docs/definition-format.md](docs/definition-format.md).
- Batched integration testing tools: `testgate` (machine-wide test slots) and `batch` (adaptive batches with bisect). See [docs/batch-tools.md](docs/batch-tools.md); plan [2026-10-03-testgate-batch.md](docs/plans/2026-10-03-testgate-batch.md).
- Delivery tools: `worktree` (per-task worktrees from an epic branch, prune of merged work) and `epic` (open, status, `--no-ff` close). See [docs/worktree-epic-tools.md](docs/worktree-epic-tools.md); plan [2026-10-03-worktree-epic.md](docs/plans/2026-10-03-worktree-epic.md).

## Layout

| Path | Purpose |
|---|---|
| [README.md](README.md) | This summary |
| [AGENTS.md](AGENTS.md) | Instructions for all agents (canonical) |
| [CLAUDE.md](CLAUDE.md) | Minimal pointer to AGENTS.md |
| [docs/](docs/) | Shared documentation; published as a markdown wiki |
| `.docs/` | Local-only notes and tracking — **git-ignored** |

## Requirements

[.NET 10+](https://dotnet.microsoft.com/download/dotnet/10.0) — tools are run with `dnx`, no further installation steps.
