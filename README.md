# agent-swarm

Pasteable **swarm definitions** that run as wide, orchestrated fleets of Claude Code agents — cheap/small models for the bulk of the work, expert (elevated-model) agents only where needed.

> Status: **early tooling, first release in preparation.** Six tools are built and tested on Windows: `swarm` (validates a swarm definition and renders Claude Code agent files, workflow scripts and a runbook), `testgate`, `batch`, `squash`, `worktree` and `epic`. Version 0.1.0 of all six is being prepared for nuget.org ([CHANGELOG.md](CHANGELOG.md), [docs/publishing.md](docs/publishing.md)); nothing has run end to end with real agents yet. See [docs/definition-format.md](docs/definition-format.md), [docs/batch-tools.md](docs/batch-tools.md), [docs/squash-tool.md](docs/squash-tool.md) and [docs/worktree-epic-tools.md](docs/worktree-epic-tools.md).

## Install

No install step: with the [.NET 10 SDK](https://dotnet.microsoft.com/download) or later, run a tool with `dnx` (use `dnx.cmd` in Git Bash). Put `--` before the tool's own arguments.

```sh
dnx AgentSwarm.Cli -- validate my-swarm.md
dnx AgentSwarm.Batch -- run tasks.json
```

| Package | Command |
|---|---|
| `AgentSwarm.Cli` | `swarm` |
| `AgentSwarm.TestGate` | `testgate` |
| `AgentSwarm.Batch` | `batch` |
| `AgentSwarm.Squash` | `squash` |
| `AgentSwarm.Worktree` | `worktree` |
| `AgentSwarm.Epic` | `epic` |

Until the first release is on nuget.org, run them from a local feed: `dotnet pack src/Swarm.sln -c Release -o feed`, then add `--add-source feed` to each `dnx` call.

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
| [CHANGELOG.md](CHANGELOG.md) | Release notes |
| [LICENSE](LICENSE) | MIT license |
| [docs/](docs/) | Shared documentation; published as a markdown wiki |
| [src/](src/) | The six tools and their libraries (`src/Swarm.sln`) |
| [tests/](tests/) | Renderer tests (`Swarm.Tests`) and tool tests (`Swarm.Tools.Tests`) |
| [.github/workflows/](.github/workflows/) | The nuget.org publish workflow |
| `.docs/` | Local-only notes and tracking — **git-ignored** |

## Requirements

[.NET 10+](https://dotnet.microsoft.com/download/dotnet/10.0) — tools are run with `dnx`, no further installation steps.
