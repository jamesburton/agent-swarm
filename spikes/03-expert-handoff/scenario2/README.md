> THROWAWAY SPIKE. **ORCHESTRATOR ONLY: do NOT show this file, `hidden/` or `builders/` output of this file to the worker or expert.**

# Spike 3 rerun, scenario2: LedgerKit (stale read-side cache, symptom far from cause)

Small C# library (`LedgerKit/`, ~290 lines in 8 files) plus xUnit tests (`LedgerKit.Tests/`, 19 visible). Billing charges arrive from feeds, are de-duplicated by id (higher `Version` corrects an earlier one), indexed per account, discounted ("first N of a SKU are half price", order dependent), taxed and rendered as a statement.

Why harder than QuotaKit: failing tests assert on `Statement` totals and line nets, so suspicion lands on `DiscountEngine`, `TaxCalculator` (banker's rounding is a deliberate, test-pinned decoy), `FeedMerger` ordering and `ChargeLedger` version handling. The cause is in the cache of a different class, and the tempting fixes live in other classes again.

## Root cause

`ChargeIndex` memoises each account's ordered charge list under the key `(account, ledger.CountFor(account))`. The key only changes when the number of charges for the account changes. A correction (`ApplyResult.Replaced`: same id, higher `Version`) swaps the stored charge but leaves the count unchanged, so the next `Statement` is served the list built before the correction (old amount, old day, old SKU, old account). A new charge in the same batch changes the count, which is why `Correction_WithAnAdditionalCharge_IsReflected` passes. The same key can also be reused later: move a charge out of an account (count 2 to 1) then add one (back to 2) and an old entry is revived.

The ledger exposes nothing that changes on a replacement, so the fix spans two classes.

## Correct fix

Give `ChargeLedger` a monotonic change counter that is bumped only when state actually changes (`Added` and `Replaced`, NOT `Ignored`), and key the cache on it:

```csharp
// ChargeLedger
public long Revision { get; private set; }
// Apply: this.Revision++; just before `return ApplyResult.Replaced;` and before `return ApplyResult.Added;`

// ChargeIndex
private readonly Dictionary<(string Account, long Rev), IReadOnlyList<Charge>> cache = new();
var key = (account, this.ledger.Revision);
```

Any equivalent "invalidate on Added/Replaced only" design is acceptable (per-account revision, a revision on the charge). A global revision evicts other accounts' entries on change; no test requires per-account warmth. (Invalidating from `BillingService.Ingest` only on `added + replaced > 0` was not run; it should also pass because all tests ingest through the service.)

## Tempting wrong fixes (all run for real)

| Fix | Why tempting | What catches it |
|---|---|---|
| A. Drop the cache (`cache.Clear()` per lookup, or bypass `ChargeIndex` in `StatementBuilder`) | Makes the 3 failures pass immediately | Visible `Index_SecondStatementWithoutChanges_HitsCache`, `Index_RedeliveredDuplicate_KeepsCacheWarm` |
| B. Add the amount sum to the key `(account, count, sum)` | Passes all 19 visible tests; looks like a fuller fingerprint | Hidden only: day-only correction, sum-neutral swap, SKU change, random sweep |
| C. Bump a revision on every `Apply` (or reset the index on every `Ingest`), including ignored redeliveries | Passes the 3 failures, "invalidate on write" | Visible `Index_RedeliveredDuplicate_KeepsCacheWarm`; hidden `Redelivery_OfOlderVersion_...` |
| (worker misdirections, in the transcript) `Version <` instead of `<=`; re-append replaced charge to arrival list | Plausible from the symptom | `Ingest_OlderOrEqualVersion_IsIgnored` fails; no effect |

Fix B is deliberately invisible to `dotnet test` on the visible suite, so a worker that found it would have declared success. It is not in the transcript; it exists to catch an expert that stops at "all visible green".

## Verify

```
dotnet test spikes/03-expert-handoff/scenario2/LedgerKit.Tests
# hidden: copy hidden/AcceptanceTests.cs.txt to LedgerKit.Tests/AcceptanceTests.cs (drop .txt)
```

Run 2026-10-02 in scratch copies (.NET SDK 10.0.401). Visible = 19 tests, hidden = 16 extra tests (35 total).

| State | Visible (19) | + hidden (35) | Failing tests |
|---|---|---|---|
| Baseline | 3 fail, 16 pass | 15 fail, 20 pass | visible: `Correction_ChangesAmount_StatementTotalFollows`, `..._LineNetFollows`, `Correction_ThenStatement_MatchesAFreshService...`; hidden: 12 more (day-only, swap, SKU change, account moves, sweeps, cache accounting) |
| A. drop cache | 2 fail | 4 fail, 31 pass | `Index_SecondStatementWithoutChanges_HitsCache`, `Index_RedeliveredDuplicate_KeepsCacheWarm`, hidden `Redelivery_OfOlderVersion_...`, `Index_ManyStatementsWithoutChanges_MissOnce` |
| B. sum in key | 0 fail (!) | 4 fail, 31 pass | hidden `Correction_DayOnly_ReordersDiscounts`, `Correction_SumNeutralSwap_IsReflected`, `Correction_SkuChange_ChangesDiscountEligibility`, `Sweep_RandomOperations_CachedStatementsEqualFreshOnes` |
| C. bump on every Apply | 1 fail | 2 fail, 33 pass | `Index_RedeliveredDuplicate_KeepsCacheWarm`, hidden `Redelivery_OfOlderVersion_...` |
| Correct fix | 19 pass | 35 pass | none |
| Transcript attempt 1 (`Version <`) | 4 fail | n/a | adds `Ingest_OlderOrEqualVersion_IsIgnored` |
| Transcript attempt 2 (re-append) | 3 fail | n/a | unchanged |
| Transcript attempt 3 (bypass index) | 2 fail | n/a | the two `Index_*` tests |
| Transcript attempt 4 (reset in `Ingest`) | 1 fail | n/a | `Index_RedeliveredDuplicate_KeepsCacheWarm` |

## Hand-off builders

`a/build-handoff.cs` and `b/build-handoff.cs` hard-code QuotaKit (project names, DST hypotheses, `DayBoundary` hunks). Patched copies live in `builders/a/` and `builders/b/` (originals untouched): they derive the test project name from `*.Tests`, and build rejected hypotheses, believed facts, open question, and `attempts.diff` from the transcript. The transcript's `## Attempt N: title (STATUS)`, `Idea:`, `Worker notes:`, a single optional ```` ```csharp ```` block, `Failed ...`/`Expected:`/`Actual:` lines, `Believed facts:`, `Open question for the expert:`, `Files touched and all reverted: ... . Working tree` formats are unchanged. Run:

```
dotnet run spikes/03-expert-handoff/scenario2/builders/a/build-handoff.cs -- <scenarioCopy> <out.md>
dotnet run spikes/03-expert-handoff/scenario2/builders/b/build-handoff.cs -- <scenarioCopy> <outDir>
```

`<scenarioCopy>` must contain `LedgerKit/`, `LedgerKit.Tests/`, `worker-transcript.md` and sit under a directory with `global.json`; leave out `README.md`, `hidden/`, `builders/`. Sizes measured: A hand-off 7,369 chars (~1,842 tok); B prompt ~181 tok plus `.handoff/` ~3,234 tok (NOTES ~600, attempts.diff ~218, last-test-run ~850, transcript copy ~1,565).

## Known leakage in the hand-off (by design)

The transcript names the area (the index and its Hits/Misses counters, "cached lists") because the tempting fixes necessarily touch it. It does not name the key, `CountFor`, a revision counter, count-versus-replacement, or that a correction does not change the count.
