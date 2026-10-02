> THROWAWAY SPIKE — worker → expert hand-off. Run 2026-10-02.

**Question:** what hand-off format best lets a stronger-model expert finish work a cheap worker got stuck on, without repeating the worker's failed attempts?

**Scenario:** `scenario/` (QuotaKit DST bug; 14 visible tests, 20 hidden; tempting `BaseUtcOffset` fix is detectable). Experts ran on `opus` in scratch copies with the answer key (`scenario/README.md`, `hidden/`) removed, one run each, 2026-10-02.

| Arm | Hand-off size | Expert tokens | Tool uses | Wall | Visible | Hidden (34 total) | Repeated wrong fix |
|---|---|---|---|---|---|---|---|
| A structured summary (`a/`) | ~1,260 tok | 54,992 | 8 | 115 s | 14/14 | 34/34 | no |
| B pointer-based (`b/`) | ~172 tok prompt + ~2,660 tok `.handoff/` | 57,117 | 8 | 107 s | 14/14 | 34/34 | no |
| C cold baseline (task only) | 0 | 51,833 | 7 | 125 s | 14/14 | 34/34 | no |

**Verdict: inconclusive.** All three arms fixed the bug with essentially the same change and similar cost; the cold baseline was cheapest. The scenario is too easy for Opus, and the worker transcript's last notes already point at the root cause ("the offset depends on the date being resolved"), so both hand-offs carried the answer. Single run per arm, so token differences (±5%) are noise.

**What this does tell us:** with a capable expert, hand-off format barely matters for an easy bug; B's prompt is ~7× smaller than A's but its folder is ~2× bigger overall, so total input is similar. Neither hurt.

**Recommended next step (not run):** a harder scenario where the transcript does *not* leak the cause and a cold expert wastes many turns, and/or cheaper experts (Sonnet) where hand-off quality should matter. Until then choose on operating cost: A is self-contained and builds deterministically; B scales better when evidence is large because the expert only reads what it needs.

---

## Rerun on harder scenario (`scenario2/`, LedgerKit), 2026-10-02

Cause lives in a different component from the symptom (cache key omits corrections; fix spans `ChargeLedger` + `ChargeIndex`); worker transcript does not leak the mechanism; two tempting fixes plus a "sum in key" fix that passes all 19 visible tests but fails 4 hidden ones. 12 runs: arms A (structured), B (pointer-based), C (cold) x Opus/Sonnet x 2, each in an isolated scratch copy without the answer key. Graded with 35 tests (19 visible + 16 hidden).

| Arm / model | Run 1 tokens (tool calls) | Run 2 tokens (tool calls) | Mean tokens | Mean tool calls | Hidden tests |
|---|---|---|---|---|---|
| A structured / Opus | 53,400 (4) | 55,623 (10) | 54,512 | 7 | 35/35, 35/35 |
| A structured / Sonnet | 52,384 (5) | 55,164 (10) | 53,774 | 7.5 | 35/35, 35/35 |
| B pointer / Opus | 56,799 (12) | 63,960 (13) | 60,380 | 12.5 | 35/35, 35/35 |
| B pointer / Sonnet | 56,198 (8) | 57,753 (13) | 56,976 | 10.5 | 35/35, 35/35 |
| C cold / Opus | 56,750 (13) | 57,081 (12) | 56,916 | 12.5 | 35/35, 35/35 |
| C cold / Sonnet | 50,922 (5) | 50,380 (3) | 50,651 | 4 | 35/35, 35/35 |

**Findings**
- **Correctness did not differ.** All 12 fixed it correctly (all 35 pass), none edited tests, none stopped at the visible-green "sum in key" trap. Every run diagnosed the cache key; fixes were a per-account revision (10) or a global revision (2 Opus runs used a ledger-wide counter, one of which noted the extra rebuilds as a trade-off).
- **Cost differences are small** (~50-60k tokens each; most of that is fixed overhead). Structured hand-off (A) cut Opus tool calls from 12.5 (cold) to 7. Pointer-based (B) was the most expensive on both models. Cold Sonnet was the cheapest overall (4 tool calls).
- **Caveats:** 2 runs per cell, so differences under ~10% are noise; the cold arm still located the cause quickly, so this scenario is still not hard; `subagent_tokens` includes fixed per-agent overhead; nothing here measures a *real* weak-worker transcript.

**Verdict:** hand-off format does not change success at this difficulty; A trims exploration for Opus, B costs more than it saves. Not evidence that hand-offs are useless, only that this repo size (~300 lines) lets a cold expert read everything cheaply.
