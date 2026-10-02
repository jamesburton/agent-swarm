# Worker transcript (recorded, small-model worker, ended STUCK)

Task given to worker: "`dotnet test` in `spikes/03-expert-handoff/scenario2/LedgerKit.Tests` has 3 failing tests. Fix the library so all tests pass. Do not delete tests."

## Attempt 0: baseline

```
$ dotnet test LedgerKit.Tests
Failed LedgerKit.Tests.BillingTests.Correction_ThenStatement_MatchesAFreshServiceWithTheSameFinalCharges
  Assert.Equal() Failure: Strings differ
  Expected: ···"\n  d2 a2 storage-gb 9.00\nSubtotal 13.00 T"···
  Actual:   ···"\n  d2 a2 storage-gb 6.00\nSubtotal 10.00 T"···
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_StatementTotalFollows
  Expected: 6.60
  Actual:   5.50
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_LineNetFollows
  Expected: decimal[]                               [10, 30]
  Actual:   ListSelectIterator<PricedLine, decimal> [10, 20]
Failed!  - Failed: 3, Passed: 16, Skipped: 0, Total: 19
```

Worker notes: all three failing tests ingest a charge, ask for a statement, then ingest a higher-version charge with the same id and ask again; the second statement still shows the first amounts. 16 other tests pass, including the ones that pin banker's rounding. I read all files under `LedgerKit/` once; nothing jumped out as wrong.

## Attempt 1: make the ledger accept equal-or-higher versions (REJECTED)

Idea: maybe the ledger is dropping the correction because `Apply` rejects it, so loosen the version check.

```csharp
/* LedgerKit/ChargeLedger.cs */
if (charge.Version < existing.Version)
```

```
$ dotnet test LedgerKit.Tests
Failed LedgerKit.Tests.BillingTests.Correction_ThenStatement_MatchesAFreshServiceWithTheSameFinalCharges
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_StatementTotalFollows
Failed LedgerKit.Tests.BillingTests.Ingest_OlderOrEqualVersion_IsIgnored
  Expected: IngestSummary { Added = 1, Replaced = 0, Ignored = 2 }
  Actual:   IngestSummary { Added = 1, Replaced = 1, Ignored = 1 }
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_LineNetFollows
Failed!  - Failed: 4, Passed: 15, Skipped: 0, Total: 19
```

Worker notes: the same 3 tests still fail and a 4th test now fails, because equal-version redeliveries are supposed to be ignored. The ingest summary already reported Replaced=1 for the correction before this change, so the version check was not the problem. Reverted.

## Attempt 2: re-append the corrected charge to the arrival list (REJECTED)

Idea: maybe a replaced charge keeps a stale slot, so remove it from the arrival list and add it again at the end.

```csharp
/* LedgerKit/ChargeLedger.cs */
this.byId[charge.Id] = charge;
this.arrival.Remove(charge.Id);
this.arrival.Add(charge.Id);
return ApplyResult.Replaced;
```

```
$ dotnet test LedgerKit.Tests
Failed LedgerKit.Tests.BillingTests.Correction_ThenStatement_MatchesAFreshServiceWithTheSameFinalCharges
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_StatementTotalFollows
Failed LedgerKit.Tests.BillingTests.Correction_ChangesAmount_LineNetFollows
Failed!  - Failed: 3, Passed: 16, Skipped: 0, Total: 19
```

Worker notes: no change at all, same 3 failures with identical messages, so this edit made no difference to the result. Reverted with `git checkout LedgerKit`.

## Attempt 3: build statements without going through ChargeIndex (REJECTED: breaks other tests)

Idea: take the lines straight from the ledger in `StatementBuilder`, ordered by day, instead of calling `ChargeIndex.ForAccount`.

```csharp
/* LedgerKit/StatementBuilder.cs, constructor now also takes the ledger */
var lines = this.discounts.Apply(this.ledger.ForAccount(account).OrderBy(c => c.Day).ToList());
```

```
$ dotnet test LedgerKit.Tests
Failed LedgerKit.Tests.BillingTests.Index_SecondStatementWithoutChanges_HitsCache
  Expected: 1
  Actual:   0
Failed LedgerKit.Tests.BillingTests.Index_RedeliveredDuplicate_KeepsCacheWarm
  Expected: 1
  Actual:   0
Failed!  - Failed: 2, Passed: 17, Skipped: 0, Total: 19
```

Worker notes: the 3 target tests pass, but the two `Index_*` tests fail because the index is no longer used and its Hits and Misses counters stay at 0. Those tests are part of the suite and I was told not to delete tests, so this is not acceptable. Reverted.

## Attempt 4: reset the index at the start of every ingest (REJECTED: breaks another test)

Idea: keep using the index but throw away its cached lists whenever `BillingService.Ingest` runs.

```csharp
/* LedgerKit/ChargeIndex.cs: public void Reset() => this.cache.Clear(); */
/* LedgerKit/BillingService.cs, first line of Ingest: */
this.Index.Reset();
```

```
$ dotnet test LedgerKit.Tests
Failed LedgerKit.Tests.BillingTests.Index_RedeliveredDuplicate_KeepsCacheWarm
  Expected: 1
  Actual:   2
Failed!  - Failed: 1, Passed: 18, Skipped: 0, Total: 19
```

Worker notes: the 3 target tests pass, but `Index_RedeliveredDuplicate_KeepsCacheWarm` fails: it expects a redelivered duplicate to leave Misses at 1, and my reset makes it 2. Making the reset conditional on the `IngestSummary` counts would need `Ingest` to call it per charge, and I could not see a clean way to do that without changing the public API. I ran out of ideas that keep all 19 green. Reverted.

## Status: STUCK

- Baseline: 3 failing (all three ingest a higher-version correction after a statement was already requested); 16 passing.
- Every change I found that makes the 3 pass breaks one or two of the `Index_*` or ingest tests; every change that keeps those green leaves the 3 failing.
- Believed facts: for the 3 failing tests `Ingest` reports Replaced=1; `DiscountEngine` and `TaxCalculator` arithmetic looked correct on a read-through; I did not find where the earlier amounts come from.
- Open question for the expert: why does a statement requested after a higher-version correction still show the earlier amounts, and what is the smallest library change that fixes that while keeping the `Index_*` and ingest tests green?
- Files touched and all reverted: `LedgerKit/ChargeLedger.cs`, `LedgerKit/StatementBuilder.cs`, `LedgerKit/BillingService.cs`, `LedgerKit/ChargeIndex.cs`. Working tree is clean at baseline.
