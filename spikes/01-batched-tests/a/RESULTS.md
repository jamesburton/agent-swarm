# Spike 1A: fixed batch (N=4) + halving bisect

> THROWAWAY SPIKE. Sandbox: 6 projects x 10 tests x 100 ms, 12 tasks, seed 1 (`--failing 0/1/2`). Windows 11, shared
> machine at ~100% CPU the whole time: wall seconds are very noisy, counts are the reliable metric.

Run: `dotnet run simulate.cs -- --root C:\Development\agent-swarm-wt\s1a --size 4 --expiry-s 10` (needs sandboxes `f0 f1 f2`,
generated with `sandbox-gen.cs`). Parts: `testgate.cs` (slots), `batch.cs` (merge, test, bisect, squash), `simulate.cs`.

| Metric | failing 0 | failing 1 (T001+T002) | failing 2 (+T003+T004) |
|---|---|---|---|
| Serial full-suite runs (1 per task) | 12 | 12 | 12 |
| Batched full-suite runs (N=4) | 3 (= ceil(12/4)) | 6 | 7 |
| Modelled cost serial (runs x 237 s) | 2844 s | 2844 s | 2844 s |
| Modelled cost batched | 711 s | 1422 s | 1659 s |
| Batched wall (raw, loaded box) | 917 s | 1946 s | 1437 s |
| Culprits isolated correctly | n/a, all 12 landed | T002 returned, 11 landed | T002+T004 returned, 10 landed |

- Mean measured full-suite gate run: 237 s (min 89 s, max 446 s, 16 runs) vs ~6 s of pure test sleep: dominated by
  incremental build + load. Serial wall was NOT executed (modelled as one run per task; batch size 1 uses the same
  code path). The first aborted attempt at serial took 494 s and 422 s for the first two tasks alone.
- Culprit rule: in a pair, the later task is returned (it fails on top of the earlier one, which lands). The seeded
  pair T001+T002 is a genuine two-task interaction, so either could be blamed; halving blames the later in queue order.
- Bisect saving: if the left half lands green and the right half is the only remainder, the right half is known red
  without a run (inferred; this is why f1 needs 6 runs, not 7).
- Squash at green: each ticket is one commit on `epic/E1` with trailers `Ticket: Txxx`, `Epic: E1`, `Batch: n`.
  Squashed tree is compared to the tested tree (no mismatch warnings seen).
- Slot gate (3 jobs of 8 s through 2 slots): waits 0.8 s, 0.25 s, 19.9 s (includes process start under load); slots 0/1/1;
  wait fraction of total time 0.19. In the batch runs the gate is used by one process, so wait was ~0 ms.
- Crashed holder: holder tree killed with `taskkill /T /F`; `owner.lock` was left behind; the next run waited 4.8 s
  (expiry lowered to 10 s for the demo, default 60 s) and logged "reclaimed stale lock" (acquired slot 0).

## Caveats

1. Counts and the modelled cost are solid; raw wall time is not (load swung a single run from 89 s to 446 s). Serial
   wall was modelled, not measured.
2. Sandbox is trivially deterministic: the only failures are the seeded pairs; no flaky tests, and every task is
   textually conflict-free, so the stop-on-conflict path (coded, returns the offender) was not exercised.
3. A failing pair costs extra runs (+3 for one pair, +4 for two) and blames one member; fixed N=4 cannot adapt to a
   high failure rate. Stale-lock check uses file mtimes; a lock reclaimed during a long GC-less hang (heartbeat >60 s
   late) would double-book a slot. Windows file-lock behaviour with a killed holder worked as intended.

Verdict: works (see `results.json`).
