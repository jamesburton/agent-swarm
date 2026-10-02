# Spike 1B results (adopted): adaptive batch + derived-touches pre-batching + halving bisect

> THROWAWAY SPIKE. Sandbox: 6 projects x 10 tests x 100 ms, 12 tasks, seed 1. Shared host at 84-100% CPU (Win32_Processor
> LoadPercentage recorded before every phase): **compare COUNTS first**, wall seconds are noisy. Raw data: `evidence/`, `results.json`.

## What was closed (all five open items, with real runs)
1. **Conflict path**: `sandbox-gen --conflicts K --overlap K --stacked K`; batch.cs stops on the first conflict, names the pair,
   writes `returned.json` (task, conflictingWith, files, git output), continues the batch, then `git rebase`s the task onto the epic tip.
2. **Derived touches**: `git diff --name-only <base>...<branch>`; `tasks.json` touches and the old `overlap-map.json` are no longer read.
3. **Serial baseline measured** (12 real suites on a green chain), not modelled.
4. **Concurrent batches** share one state dir with `--slots 1`: serialised, no deadlock, killed holder reclaimed.
5. **Failure edges**: `edge-cases.ps1`, 6 of 6 cases pass (exit code + one-line message asserted, 60 s hang guard).

## Evidence table
| Item | Counts | Wall / notes |
|---|---|---|
| Serial f0 (measured) | 12 suites, 12 landed | 889 s (load 100%) |
| Batched f0 | 2 suites (sizes 4, 8), 12 landed | 155 s (load 84%): 5.7x measured, 6x by count |
| One warm suite | 1 | 70.7 s (so 12 x 70.7 = 848 s matches the 889 s serial) |
| Bisect regression f1 / f2 | 7 / 8 suites (3 / 4 bisect, 1 / 2 inferred), culprits T002 / T002+T004 | unchanged vs the previous run |
| Conflicts `conf` (2 conflict + 1 stacked pair) | landed 10, returned 3, rebased-and-landed 1 (T012), needs-worker 2 (T008, T010); 3 suites, 4 batches | the 3 b-tasks conflicted in one batch: that batch ran NO suite |
| Overlap `ov`, pre-batched | landed 11, returned 2, rebased-and-landed 1 (T012), needs-worker 1 (T006); 4 suites | overlap pairs together at first placement: 0 of 4 |
| Overlap `ov`, naive fixed-4, no pre-batch | same 11 / 2 / 1 / 1; 4 suites | overlap pairs together: 3 of 4 (all 4 in same fixed chunk) |
| Derived touches | `conf` run used a tasks.json whose touches were all `bogus/Ignored.cs`; summary `derivedTouches` shows the real files and pairs were still separated | |
| Concurrent (2 batch.cs, `--slots 1`, one state dir) | 4 suites, 0 overlapping holds, waitMs 45 / 93285 / 27575 / 16675, both instances landed 6/6, exit 0 | slot busy 99.5% from first acquire (91.5% from launch) |
| Killed holder (expiry 8 s, heartbeat 1 s) | holder c2b's testgate tree killed mid-suite; c2a `reclaimed: true` | 10.9 s after the kill; c2b exited 4 with a one-line error; c2a landed 6/6 |
| Edges | a all conflict: exit 1, 0 suites, needs-worker 2; b empty: exit 0; c missing branch: exit 3; d state path >200 chars: exit 2 (batch and testgate); e bad JSON: exit 3 | no hangs |

## Findings that change the picture
- **Pre-batching does not avoid textual conflicts and did not cut suite runs** (4 vs 4 on `ov`). It separates same-file tasks, so a red batch never
  mixes overlapping edits, and it moves a stacked task's conflict from land time (naive: the suite tested T012 merged, then the squash
  failed, so the tested tree differed from the landed tree) to merge time (before any suite). Cost: it can add a batch.
- **Rebase rescues only stacked tasks** (parent already squash-landed; git drops the parent patch). Same-line conflicts conflict again
  and become `needs-worker`; no rebase can fix those. Rebasing runs on a copy ref `rebased/E1/Txxx`, so worker branches are untouched.
- The earlier modelled f0 count was 3 suites because of synthesized overlaps; with real (derived) touches it is 2.
- Windows long paths: .NET created a 297-char directory (LongPathsEnabled=1) but `git -C` on it fails "Filename too long", hence the 200-char guard.

## Caveats (top three)
1. Wall time: serial ran at load 100%, batched at 84%, so the 5.7x is slightly flattering; counts (12 vs 2) are the reliable number.
2. Conflicts and overlaps are generator-made and synthetic; real LLM-worker edits will conflict at different rates and shapes.
3. Culprit blame is the later task of an interacting pair; the two-simultaneous-reclaimers race is still unstressed.

## What remains open
- Real repo with real overlapping edits; prediction of conflicts from hunk ranges instead of file names.
- Rebase of multi-commit tasks with semantic (non-textual) conflicts; who owns the rebased ref (see `PROMOTION.md` decisions).
- More than two concurrent gates, fairness (no FIFO), max-wait timeout; two simultaneous reclaimers.
- `simulate.cs` (old driver) was not re-run; its numbers were reproduced by `regress.ps1` for f1/f2 only.

## How to run
`pwsh -File serial-vs-batched.ps1`, `conflicts-overlap.ps1 -Which conf|ov`, `concurrent.ps1`, `regress.ps1`, `edge-cases.ps1`, then `make-results.ps1`.
Sandboxes live under `C:\Development\agent-swarm-wt\s1c\` (short paths).
