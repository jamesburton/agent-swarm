# Changelog

All six tools share one changelog. Each tool has its own package version; the first public release puts all of them at `0.1.0`.

## 0.1.0 — first public release (unreleased)

The first nuget.org release of the agent-swarm tools. Each runs with `dnx <package-id>` and needs only the .NET 10 SDK or later.

| Package | Command | What it does | Docs |
|---|---|---|---|
| `AgentSwarm.Cli` | `swarm` | Validates a swarm definition (Markdown, YAML or C#) and renders Claude Code agent files, workflow scripts and a runbook. | [definition-format.md](docs/definition-format.md) |
| `AgentSwarm.TestGate` | `testgate` | Runs an expensive command (normally the full test suite) under a machine-wide slot limit, with heartbeats and stale-lock reclaim. | [batch-tools.md](docs/batch-tools.md) |
| `AgentSwarm.Batch` | `batch` | Merges task branches into an integration worktree, runs the suite once per batch, bisects red batches and lands the green tasks on an epic branch. | [batch-tools.md](docs/batch-tools.md) |
| `AgentSwarm.Squash` | `squash` | Lands a task (or a ticket inside a stack) on its epic as one trailer-stamped commit with exactly the tree `batch` tested. It is the default lander inside `batch`, and also runs on its own as `squash run`. | [squash-tool.md](docs/squash-tool.md) |
| `AgentSwarm.Worktree` | `worktree` | Creates per-task git worktrees from an epic branch, lists them with their merge state, and prunes merged work. | [worktree-epic-tools.md](docs/worktree-epic-tools.md) |
| `AgentSwarm.Epic` | `epic` | Opens epic branches, reports what blocks closing them, and closes them with a trailer-listed `--no-ff` merge onto the active branch. | [worktree-epic-tools.md](docs/worktree-epic-tools.md) |

### Shared behaviour

- One strict config file, `.swarm/batch.json`, read by testgate, batch, squash, worktree and epic. Unknown keys are errors.
- Machine-readable JSON on stdout and progress on stderr, with documented exit codes per tool.
- Branch naming templates (`{kind}/{id}-{slug}`) with optional `allowedPrefixes`, for CI pipelines that trigger only on exact prefixes.

### Tested on

- Windows 11, .NET 10, git 2.54 (reftable repositories): 541 renderer tests and 550 tool tests, plus local-feed `dnx` smoke runs of every tool ([dnx-invocation-notes.md](docs/dnx-invocation-notes.md)).
- **Not tested:** Linux and macOS, git versions other than 2.54, and a full end-to-end run with real Claude Code agents.

### Known limitations

Each tool's page has a Known limitations section. The ones most likely to matter:

- `worktree` prune and `epic close` do not clean up after a removal blocked by a held file (Windows); a human inspects what is left.
- A crashed local `batch` or `squash run` blocks `epic close` until its lock is older than `expirySec`.
- `batch` integration merges and the `epic close` merge commit skip git hooks (`--no-verify`).
