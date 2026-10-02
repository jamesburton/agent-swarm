# agent-swarm

Pasteable **swarm definitions** that run as wide, orchestrated fleets of Claude Code agents — cheap/small models for the bulk of the work, expert (elevated-model) agents only where needed.

> Status: **scoping**. This repo starts as a capability study and design space, then grows into reusable definitions and tooling.

## Goals

1. **Define swarms as text.** A swarm (roles, models, tools, instructions, hand-offs) is a document you can paste into a session and run.
2. **Optimise tokens and speed.** Small models (e.g. Haiku/Sonnet) with tight, well-written instructions do the parallel fan-out; experts are summoned on demand.
3. **Elevate on demand.** Clone any worker into an expert variant on a stronger model — forked context by default, shared context as an opt-in.
4. **Trusted sign-off.** Understand and use what is (and is not) possible for approval/hand-off between sub-agents, other local sessions, and Remote Control (RC) / cloud sessions.
5. **Lean orchestration and clean history.** Small agents under orchestrators/sub-orchestrators; worktree-isolated tasks, batched integration testing (one full-suite run per batch, bisect on failure), ticket-sized squashed commits per epic branch, merged `--no-ff` to the active branch. See [docs/workflow.md](docs/workflow.md).
6. **Clean tooling.** Tools are launched with minimal ceremony, e.g. `dnx <our-nuget-id>`, so the only setup requirement is a link to .NET 10+.

## Scope of the first pass

- Capability matrix: what we can and can't refine about sub-agents; what we can and can't do with cross-session communication and sign-off. See [docs/capabilities.md](docs/capabilities.md).
- Overview of recent work (last 6 months, i.e. since 2026-04) in the parent `C:\Development` folder that this builds on. See [docs/landscape.md](docs/landscape.md).
- A first swarm-definition format and a worked example.

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
