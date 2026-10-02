---
created: 2026-10-02
updated: 2026-10-02
status: current
---
# Landscape: related work in `C:\Development`

Window: last 6 months (since 2026-04-02). One-line summaries from a quick read of each project's README, **not yet reviewed in depth**. Dates are last activity.

| Project | What it is | Relevance to agent-swarm | Last activity |
|---|---|---|---|
| `skills` | Workshop for developing, testing and sharing agent skills | Source of reusable role instructions for swarm members | 2026-10-01 |
| `Gryffin` | Enterprise AI platform on .NET 10 / Aspire (projects, sessions, teams) | Multi-tenant orchestration and session isolation patterns | 2026-10-02 |
| `dotLLM` | Native C#/.NET LLM inference engine (CUDA/CPU/Vulkan) | Local small-model backend for cheap fan-out workers | 2026-09-30 |
| `SessionTracker` | Session state tracking (just initialised) | Overlaps with cross-session coordination and sign-off | 2026-09-29 |
| `AFClaude` | Local .NET 10 process exposing Azure AI Foundry models to Claude via MCP | Model-serving bridge; non-Anthropic worker models | 2026-09-23 |
| `rowboat` | Shared-space personal assistants, one agent per teammate | Multi-agent coordination reference | 2026-09-17 |
| `magnitude` | Local-model inference server integrated with several agent CLIs | Local model routing for workers | 2026-09-15 |
| `LiteLLMSwarm` | Swarm manager for local/remote LLM services: routing, caching, escalation to advisor models | Closest prior art for tiered "cheap worker → expert advisor" escalation | 2026-05-08 |
| `HolyClaude` | Containerised AI dev workstation with Claude Code | Possible isolated runner environment | 2026-04-10 |
| `DnxDirectory` | Local-first directory of `dnx`-runnable tools with vector search and runtime verification | Direct fit for the "clean `dnx` tools" goal | 2026-04-08 |
| `ClawSharp` | .NET 10 Claw agent framework on Aspire with the MCP C# SDK | Agent framework reference | 2026-04-07 |
| `claude-vector-context-stash` (ccstash) | Vector context store for Claude Code, retrieved via MCP | Compact context hand-off when elevating a worker to an expert | 2026-07-03 |
| `CSharpDocAgentFrameworkMCP` | Code documentation as agent-consumable memory over MCP | Cheap code understanding for small-model workers | 2026-07-03 |
| `skills-guru` | Skill manager (install, scope, sync, audit) | Skill lifecycle for swarm role definitions | 2026-07-02 |

Out of window but noted: `maestro` (GitHub Projects orchestrator with Claude agents in worktrees, 2026-03) and `project-scaffolder` (eight specialised .NET agents, 2025-12).

## First observations

- `LiteLLMSwarm` + `DnxDirectory` + `ccstash` already cover three of our pillars (tiered models, `dnx` tools, compact context hand-off); the swarm-definition format is the missing glue.
- Sign-off must be policy- or human-based, per [capabilities](capabilities.md#3-cross-session-communication-and-sign-off); `SessionTracker` is worth reviewing for tracking state around it.
