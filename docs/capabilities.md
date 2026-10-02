---
created: 2026-10-02
updated: 2026-10-02
status: current
---
# Capabilities and limits

Status of every claim: **documented** (per code.claude.com docs, fetched 2026-10-02) unless marked **unverified**. Nothing here has yet been tested in this repo; see [Verification backlog](#verification-backlog).

## 1. Sub-agent definition

| Can refine | Notes |
|---|---|
| `model` | Alias (`haiku`/`sonnet`/`opus`/`fable`), full ID, or `inherit`. **Per-call override** via the Agent tool beats the definition. |
| `tools` / `disallowedTools` | Allow/deny lists incl. `mcp__server__*`. |
| `skills` | Preloads full skill content into the sub-agent. |
| `mcpServers` | Names or inline definitions (ignored for plugin sub-agents). |
| `permissionMode` | `default`, `acceptEdits`, `auto`, `dontAsk`, `bypassPermissions`, `plan`, `manual` (ignored for plugin sub-agents). |
| `maxTurns`, `effort`, `background`, `memory`, `color`, `omitClaudeMd`, `initialPrompt` | `effort`: low→max. `initialPrompt` only when run as main session via `--agent`. |
| `isolation: worktree` | Own git worktree, branched from the **default branch**, not parent HEAD. |
| `hooks` | Only `PreToolUse`, `PostToolUse`, `Stop` (→ `SubagentStop`). |

Model resolution order: per-call `model` → definition `model` → `CLAUDE_CODE_SUBAGENT_MODEL` → main model. `CLAUDE_CODE_SUBAGENT_MODEL_FORCE=1` overrides definitions and Claude's per-call choice (not forks).

**Cannot / limits**
- Nesting depth defaults to 3 (`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`; 1 disables). `Agent(type,…)` allowlists only apply to `--agent` main threads.
- Agent-team teammates cannot spawn teammates.
- SDK `AgentDefinition` has no `hooks`/`isolation`/`color` — **unverified** (only the guide table was read).

### Forks (the "elevate an expert" question)
- A fork inherits parent system prompt, tools, **model**, full history, and shares the prompt cache. Cannot spawn forks; may use worktree isolation.
- **A fork is always on the parent's model** — `CLAUDE_CODE_SUBAGENT_MODEL_FORCE` exempts forks. So "fork the context *and* raise the model" is **not** a single native operation.
- No separate "shared context" mode exists beyond the fork's shared cache.
- On by default interactively (v2.1.232+), off in `-p`/SDK (`CLAUDE_CODE_FORK_SUBAGENT=1|0`).

**Implication for us:** to elevate a worker we spawn a *fresh* sub-agent on the stronger model and hand it a distilled context (summary + pointers), or run the whole orchestrator on the expert model and fork cheap-model work out. Designing the hand-off format is a core deliverable.

## 2. Parallelism

| Mechanism | Limit / behaviour |
|---|---|
| Sub-agents | 20 concurrent by default (`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS`); background by default. |
| `Workflow` scripts | JS with `agent()`, `pipeline()`, `parallel()`, `phase()`. 16 concurrent agents (env 1–256), 1,000 agents/run, 4,096 items/call. Per-stage model. No mid-run user input. Saved in `.claude/workflows/`. Needs explicit opt-in. |
| Agent teams | Experimental (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`), interactive only. Peer mailboxes + shared task list. One team/session, no nesting; docs suggest 3–5 teammates. |
| Remote Control server | `claude remote-control --spawn same-dir\|worktree\|session`, default capacity 32. |

## 3. Cross-session communication and sign-off

**Possible**
- `ListAgents` / `SendMessage` between local sessions (same-machine socket/named pipe; Windows needs v2.1.234+), and to RC/cloud sessions via Anthropic servers (claude.ai sign-in required). Plain text, ~1M chars.
- Offline recipients get the message on reconnect.
- Inbound policy via `crossSessionInbound` = `accept` | `hold` | `refuse`; `isolatePeerMachines` forces your approval for outbound.
- A **channel** (MCP server with `claude/channel/permission`) can relay permission prompts to a human-controlled sender who can approve/deny. Research preview, allowlisted.
- `PermissionRequest` **hooks** can auto-allow/deny by configured policy.

**Not possible**
- A peer-session (or sub-agent/teammate) message **cannot approve a permission prompt, change config, or run slash commands**. The receiver's own rules always apply and Claude is told not to relay denied actions elsewhere. So "trusted sign-off" cannot be a message from another agent.
- No cross-container, or WSL↔Windows, session reach.
- Messages from a session not on RC carry no reply address (one-way).
- Remote Control's approval model for remote clients — **unverified** (not read).
- Whether `PermissionRequest` hooks fire in `-p` mode — **unverified**.

**Practical sign-off design:** sign-off is *policy* (allow rules, `PermissionRequest` hooks, `permissionMode`) or a *human* (channel / RC client), never an agent claim. Swarm definitions should express sign-off as a declared gate that maps onto one of those.

## 4. Models

- Haiku/Sonnet/Opus/Fable per sub-agent, per call, or fleet-wide (`CLAUDE_CODE_SUBAGENT_MODEL`). Aliases can be repointed with `ANTHROPIC_DEFAULT_*_MODEL`.
- Non-Anthropic models only via an [LLM gateway](https://code.claude.com/docs/en/llm-gateway) plus `ANTHROPIC_CUSTOM_MODEL_OPTION`; no native provider. Per-sub-agent custom model strings — **unverified**. (Relevant: `LiteLLMSwarm`, see [landscape](landscape.md).)

## 5. `dnx` tooling

- `dnx <PackageId>[@ver] [-- args]` = `dotnet tool exec` (.NET 10.0.100+): fetches to NuGet cache, runs, no install, no PATH change.
- To ship an MCP server: `dotnet new mcpserver`, `<PackageType>McpServer</PackageType>`, `.mcp/server.json` (`registry_name: nuget`), `dotnet pack`/`push`. Consume with:

```json
{ "mcpServers": { "name": { "command": "dnx", "args": ["Pkg.Id", "--version", "1.0.0", "--yes"] } } }
```

- **Unverified conflict:** MS docs say `--yes` is .NET 11+, but MS blog/READMEs use it on .NET 10. Without it the confirmation prompt would hang a stdio launch — test on our SDK.
- No Claude Code doc covers `dnx` directly; it is just a stdio `command` — **unverified** on Windows shell resolution.

## Verification results (probe 0, 2026-10-02; see [spikes/00-probes/RESULTS.md](../spikes/00-probes/RESULTS.md))

- **verified:** `dnx` has no `--yes` flag on SDK 10.0.401 or 11.0.0-rc.1 and does not prompt with stdin closed (one package tested); bare `dnx` fails in the Git Bash tool but `dnx.cmd` and PowerShell work; per-call `model: haiku` override works; `PermissionRequest` hooks fire in `claude -p` and an `allow` decision is honoured (latency of 237 s unexplained).
- **documented:** forks stay on the parent model; SDK `AgentDefinition` has no `hooks`/`isolation`/`color`, so hooks are session-level.
- **still unverified:** Remote Control's approval model; per-sub-agent custom model strings through a gateway; generated Workflow scripts have only been syntax-checked.
