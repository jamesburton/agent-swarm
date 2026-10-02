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
