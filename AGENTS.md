# Agent Handbook — agent-swarm

Canonical instructions for all agents (Claude, Codex, Gemini, …). `CLAUDE.md` only points here.

- Project summary and goals: [README.md](README.md)
- Shared docs (published wiki): [docs/](docs/) — start at [docs/capabilities.md](docs/capabilities.md); delivery design in [docs/workflow.md](docs/workflow.md); decisions in [docs/decisions.md](docs/decisions.md); design spec [docs/specs/2026-10-02-agent-swarm-design.md](docs/specs/2026-10-02-agent-swarm-design.md); plans [spike phase](docs/plans/2026-10-02-spike-phase.md), [definition + renderer](docs/plans/2026-10-03-swarm-definition-renderer.md); [definition format and `swarm` tool](docs/definition-format.md); [dnx invocation notes](docs/dnx-invocation-notes.md); [testgate and batch tools](docs/batch-tools.md) and plan [testgate + batch](docs/plans/2026-10-03-testgate-batch.md); [squash tool](docs/squash-tool.md) and plan [squash](docs/plans/2026-10-03-squash.md); [worktree and epic tools](docs/worktree-epic-tools.md) and plan [worktree + epic](docs/plans/2026-10-03-worktree-epic.md)
- Machine-wide conventions: `~/AGENTS.md`

## Conventions

- Windows host; PowerShell-native commands, Git Bash available.
- `docs/` is shared and published as a markdown wiki: plain GitHub-flavoured markdown, relative links, no private details.
- `.docs/` is **git-ignored** local scratch/tracking. Never put anything there that other contributors need.
- Mark claims about Claude Code behaviour as **verified** (tested here) or **documented** (per docs) or **unverified**; record the source.
- Tools are invoked via `dnx <package-id>` (needs .NET 10+); don't write install steps beyond linking the .NET requirement.
- Delegation: pass `model` explicitly — `haiku` simple lookups, `sonnet` standard work, `opus` complex reasoning.
- Don't add features or refactors beyond what was asked.
- Docs and memories are self-evolving: light front-matter, `created`/`updated` timestamps, `[STALE?]` marking, archive when obsolete, review on encounter. See [docs/doc-lifecycle.md](docs/doc-lifecycle.md).
