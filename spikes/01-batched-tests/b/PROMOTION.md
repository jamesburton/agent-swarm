# Promoting testgate.cs and batch.cs to `dnx` tool packages

> THROWAWAY SPIKE notes. Lists what must change; none of it is implemented. Probe 0: `dnx` has no `--yes`; use `dnx.cmd` under Git Bash.

## Packages and CLI surface
| Tool (package id TBD) | Command | Notes |
|---|---|---|
| `Swarm.TestGate` | `testgate run [--config f] [--slots N] [--state dir] [--cwd d] -- <cmd...>` | Add `status` (list holders, ages) and `reclaim --force`. |
| `Swarm.Batch` | `batch run <tasks.json> [--config f] [--epic E1] [--base main] [--mode batched\|serial] [--json]` | Drop `<sandbox>` arg (use cwd repo); drop `measure`/`--no-prebatch`/`--fixed` into `--experimental-*`. |

Replace the hard-coded `dotnet test Sandbox.slnx` with `testCommand` from config, defaulting to the affected-tests runner (stage 3).
Both tools become library + thin CLI (`System.CommandLine`), so the plugin and a future `swarm run` share code.

## Config file (`.swarm/batch.json`, overridable by flags; flags win)
`{ "slots": 2, "batch": { "start": 4, "min": 2, "max": 8 }, "expirySec": 60, "heartbeatSec": 5, "pollMs": 200,`
`"baseBranch": "main", "epic": "E1", "testCommand": ["dotnet","test"], "stateDir": ".docs/runs", "worktreeRoot": "C:\\Development\\agent-swarm-wt", "maxRebaseAttempts": 1 }`
State dir resolves the MAIN worktree's `.docs/runs/` via `git rev-parse --git-common-dir` (decision, stage 2); one dir per machine => one slot budget.
Validate on load: slots >= 1, min <= start <= max, expiry >= 3 x heartbeat, state path <= 200 chars (spike already enforces the last).

## Exit codes (currently 0/1/2/3/4; keep, document, version)
0 all tasks landed | 1 ran, some tasks returned/rejected (not a tool failure) | 2 usage/config | 3 bad input (tasks, branch, base) | 4 environment (git, gate, worktree) | add 5 = gate wait timeout (`--max-wait`, none today: a wedged live holder blocks forever).
Errors: ONE line on stderr, `error: <what> (<hint>)`.

## JSON output (stdout, one object; add `schemaVersion`)
`summary.json`: mode, tasks, fullSuiteRuns, bisectRuns, inferredRedSkipped, batches, sizeTrace, tasksLanded, returned, rebasedAndLanded, needsWorker, rejectedRed, suites[{label,waitMs,runMs,slot,reclaimed,acquiredUtc,releasedUtc}], landed[], rejected[], derivedTouches.
`returned.json` (per task): task, kind (conflict|red), stage (merge|land|suite), batch, conflictingWith[], files[], gitOutput, rebase, rebaseOutput, final (rebased-and-landed|needs-worker|returned-red). Make it append-only JSONL so a worker can watch it.
Testgate line: `{waitMs, runMs, slot, exitCode, reclaimed, acquiredUtc, releasedUtc}`.

## Logging
Human progress on stderr (as now); add `runs/<id>/events.jsonl` (batch start/merge/conflict/suite/bisect/land/rebase, timestamps) and `--verbosity quiet|normal|detail`; keep per-suite logs, add retention (they grow without bound).

## Design decisions the human must make
1. **Missing task branch**: spike aborts the whole batch before merging (exit 3). Alternative: return that task to its worker and run the rest.
2. **Who rebases**: the tool rebases a COPY ref (`rebased/E1/Txxx`) and lands that, leaving the worker branch intact. Decide: does the worker adopt the copy, is a clean-rebase requeue automatic, max attempts (spike: 1).
3. **Stacked tasks**: squash-at-green cannot replay a task that contains an already-landed parent commit (spike returns it, rebase drops the parent). Decide: forbid stacking, land stacks as one unit, or keep return+rebase.
4. **Blame**: culprit = the later task of an interacting pair (bisect order), not a true attribution; decide if the first/both go back.
5. **Pre-batching value**: derived touches separate same-file tasks but do NOT prevent textual conflicts (see RESULTS.md); decide whether to predict conflicts from hunk ranges (`git diff -U0`) instead of file names, or drop pre-batching.
6. **Gate scope**: file locks work on one machine only; shared/remote runners need a different backend. Reclaim has a small documented two-reclaimer race.
7. **Wait policy**: no max wait or fairness (FIFO) today; a long suite starves nobody only because polling is random.
8. **Exit 1 vs 0** when everything landed after a rebase (spike: 0).
