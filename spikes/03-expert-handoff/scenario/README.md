> THROWAWAY SPIKE

# Spike 3 shared scenario: QuotaKit (seeded DST bug)

Small C# library (`QuotaKit/`) plus xUnit tests (`QuotaKit.Tests/`). Per-tenant daily quotas where the "day" is the tenant's local day. Orchestrator-facing notes; do NOT show this file to the worker or expert.

## Root cause

`DayBoundary.StartOfDay` builds local midnight as `new DateTimeOffset(local.Date, local.Offset)`. `local.Offset` is the offset in force at `instant`, not at midnight. On a DST transition day the two differ by the DST delta, so the computed day start is one hour early (spring forward) or late (fall back).

## Correct fix

Resolve the offset at the midnight being built: `tz.GetUtcOffset(midnightUnspecified)`; for an ambiguous midnight (e.g. America/Havana fall back) use `tz.GetAmbiguousTimeOffsets(midnight).Max()` (first occurrence). An invalid midnight (Havana spring forward) yields the standard offset, which is exactly the transition instant. `StartOfNextDay`, `NextReset`, `DayLength` are fixed transitively.

## Tempting wrong fixes

- Edit the test expectation (the New York test then passes, London still fails).
- `if (tz.IsDaylightSavingTime(instant)) start += 1h` or anchor to `tz.BaseUtcOffset`: both pass the 2 visible failures but are wrong on every ordinary summer day, so `Used_SummerDay_NewYork_StartsAtLocalMidnight` and `NextReset_RegularDay_IsNextLocalMidnight` fail. The `BaseUtcOffset` variant was verified; the transcript shows both.

## Verify

```
dotnet test spikes/03-expert-handoff/scenario/QuotaKit.Tests
```

| State | Visible (14 tests) | + hidden (34 tests) |
|---|---|---|
| Baseline | 2 fail (the `*_SpringForwardDay_*` tests), 12 pass | 17 fail, 17 pass |
| Tempting fix (`BaseUtcOffset`) | 2 fail (summer day, NextReset regular day), 12 pass | 13 fail, 21 pass |
| Correct fix | 14 pass | 34 pass |

Hidden tests: copy `hidden/AcceptanceTests.cs.txt` to `QuotaKit.Tests/AcceptanceTests.cs`. They cover fall back, Sydney, Lord Howe (30-minute DST), Havana (midnight invalid and ambiguous), `DayLength` 23/24/25h, and a year-long property sweep over seven zones. IANA zone ids work on Windows 11 with .NET 10 (ICU). Run the suite in a scratch copy when verifying fixes; `bin/` and `obj/` are git-ignored.

## Files

- `worker-transcript.md`: recorded failed attempts (4) ending STUCK, including the rejected tempting fix and why.
- `QuotaKit/`: `DayBoundary.cs` (bug), `DailyQuota.cs`, `QuotaPolicy.cs`.
- `hidden/AcceptanceTests.cs.txt`: not compiled.
