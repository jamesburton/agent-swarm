# Spike 1B results: adaptive batch + overlap pre-batching + halving bisect

> THROWAWAY SPIKE. Sandbox: `--projects 6 --tests-per 10 --delay-ms 100 --seed 1 --tasks 12`, `--failing 0/1/2`, 2 slots.
> The host was at ~100% CPU from other sessions: raw wall times are 2-3x the unloaded cost. Compare COUNTS and MODELLED cost.

| Metric (12 tasks) | Serial (1 suite/task) | Failing 0 | Failing 1 | Failing 2 |
|---|---|---|---|---|
| Full-suite runs | 12 | 3 | 7 | 8 |
| of which bisect runs | - | 0 | 3 | 4 |
| Red runs inferred, not executed | - | 0 | 1 | 2 |
| Modelled cost (runs x 142.8 s) | 1714 s | 428 s | 1000 s | 1142 s |
| Raw wall time (loaded host) | not run | 869 s | 2451 s | 2984 s |
| Batch size trace | 1 | 4,8,8 | 4,2,4,8 | 4,2,4,8 |
| Culprit isolated correctly | n/a | yes (none) | yes (T002) | yes (T002, T004) |

Single warm full suite measured through testgate: 142.8 s (138 s on the first measure). Modelled saving vs serial: 75% / 42% / 33%.

| Other metric | Result |
|---|---|
| Pre-batch separated overlaps | 3 of 3 synthesized overlapping pairs split across batches (0 together); naive fixed chunks of 4 would put 3 of 3 in the same batch |
| Crashed-lock recovery | Holder tree killed with `taskkill /F /T`; new run reclaimed the slot, waitMs 2647 (demo expiry 6 s, default 60 s) |
| Slot gate | 3 concurrent 4 s jobs through 2 slots: waits 8 / 56 / 10973 ms, wait fraction 0.258, both slots used |
| Slot utilisation inside batch.cs | 1.0 wait fraction ~0: batches run one at a time, so the gate never queued them (no contention to measure) |
| Squash | One commit per ticket on `epic/E1` with trailers `Ticket: Txxx`, `Epic: E1`, `Batch: n` (checked on f2) |

## How it works
- Adaptive size: start 4, x2 after a green batch (max 8), /2 after any red batch (min 2).
- Pre-batcher: queue order, skips a task whose `touches` intersect one already in the batch; skipped tasks go to a later batch.
- Merge sequentially into worktree `epic/E1`; a conflict aborts that merge, reports the offender, rest continue.
- Red batch: halve; if the left half lands green, the right half on top is the already-seen red state, so that run is inferred (saves a suite run).

## Caveats
1. **Synthesized data**: generator tasks touch distinct files, so `overlap-map.json` adds fake overlapping `touches` (T006, T008, T012). Real overlap rates are unknown. Pre-batching here removes conflicts that would not have occurred in this sandbox anyway.
2. **Serial baseline wall time was not executed** for the final run (modelled as 12 x single suite). A partial earlier serial run took ~490 s per suite under heavier load, so raw wall is not comparable; use run counts.
3. Seeded failure is a pairwise interaction: the pair's second task (T002, T004) is rejected, the first lands. Merge-conflict path was not exercised (no conflicts arise). Only 12 tasks and one crash-demo run; the 3-batch f0 plan means f0's 75% saving depends on max 8 and no reds.
4. Lock reclaim has a small theoretical race between two simultaneous reclaimers (documented in testgate.cs); not stressed.
