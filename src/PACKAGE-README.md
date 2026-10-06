# agent-swarm tools

Part of [agent-swarm](https://github.com/jamesburton/agent-swarm): pasteable swarm definitions that run as orchestrated fleets of Claude Code agents. Each tool runs with `dnx` and needs only [.NET 10+](https://dotnet.microsoft.com/download).

| Package | Command | Purpose |
|---|---|---|
| `AgentSwarm.Cli` | `swarm` | Validate a swarm definition (Markdown, YAML or C#) and render agent files, workflow scripts and a runbook |
| `AgentSwarm.TestGate` | `testgate` | Run an expensive command (normally the full test suite) under a machine-wide slot limit |
| `AgentSwarm.Batch` | `batch` | Merge task branches into an integration worktree, test once per batch, bisect red batches, land green tasks |
| `AgentSwarm.Squash` | `squash` | Land a task branch on its epic as one trailer-stamped commit with the tested tree |
| `AgentSwarm.Worktree` | `worktree` | Create, list and prune per-task git worktrees from an epic branch |
| `AgentSwarm.Epic` | `epic` | Open, report on and close epic branches with a `--no-ff` merge |

```sh
dnx AgentSwarm.Batch -- --help
```

Documentation: [docs/](https://github.com/jamesburton/agent-swarm/tree/main/docs). License: MIT.
