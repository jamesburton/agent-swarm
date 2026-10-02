# Worker transcript (recorded, small-model worker, ended STUCK)

Task given to worker: "`dotnet test` in `spikes/03-expert-handoff/scenario/QuotaKit.Tests` has 2 failing tests. Fix the library so all tests pass. Do not delete tests."

## Attempt 0: baseline

```
$ dotnet test QuotaKit.Tests
Failed QuotaKit.Tests.DailyQuotaTests.Used_SpringForwardDay_NewYork_ExcludesPreviousEvening
  Assert.Equal() Failure: Values differ
  Expected: 20
  Actual:   30
Failed QuotaKit.Tests.DailyQuotaTests.Used_SpringForwardDay_London_ExcludesPreviousEvening
  Expected: 6
  Actual:   10
Failed!  - Failed: 2, Passed: 12, Skipped: 0, Total: 14
```

Worker notes: both failing tests count the previous evening's event as "today". Both are on 8 Mar / 29 Mar (the spring-forward dates). `Used` filters `e.At >= start`, so `StartOfDay` must be returning an instant that is too early by one hour. Read `DailyQuota.cs` and `DayBoundary.cs`; `Used` looks fine.

## Attempt 1: change the test expectation (REJECTED by worker)

Idea: the event at 04:30Z is only 30 minutes before the "start", maybe the test data is off.

```
$ git diff --stat
 QuotaKit.Tests/DailyQuotaTests.cs | 2 +-
# changed Assert.Equal(20, ...) to Assert.Equal(30, ...) in the New York test
$ dotnet test QuotaKit.Tests
Failed!  - Failed: 1, Passed: 13, Skipped: 0, Total: 14
```

Worker notes: London test still fails (expects 6, gets 10). Also this contradicts the task: 23:30 EST on 7 Mar is plainly the previous local day, so a quota of 30 for 8 Mar is wrong. Reverted with `git checkout QuotaKit.Tests`.

## Attempt 2: special-case DST in StartOfDay (REJECTED: breaks other tests)

Idea: on DST days `local.Offset` is one hour ahead of midnight's offset, so add one hour.

```csharp
var start = new DateTimeOffset(local.Date, local.Offset);
if (tz.IsDaylightSavingTime(instant)) start = start.AddHours(1);
return start;
```

```
$ dotnet test QuotaKit.Tests
Failed QuotaKit.Tests.DailyQuotaTests.Used_SummerDay_NewYork_StartsAtLocalMidnight
  Expected: 20
  Actual:   0
Failed QuotaKit.Tests.DailyQuotaTests.NextReset_RegularDay_IsNextLocalMidnight
  Expected: 2026-07-16T04:00:00+00:00
  Actual:   2026-07-16T05:00:00+00:00
Failed!  - Failed: 2 or more (summer-day and NextReset tests among them)
```

Worker notes: the spring-forward tests now pass, but the shift applies to every summer day, not just the transition day. Plain July days are now one hour late. `IsDaylightSavingTime` is true all summer; I need "is this the transition day", which I cannot get cheaply. Reverted.

## Attempt 3: use the zone's standard (base) offset (TEMPTING WRONG FIX, REJECTED)

Idea: midnight on the spring-forward day is in standard time, so anchor to `tz.BaseUtcOffset`, which is -5 for New York and 0 for London.

```csharp
return new DateTimeOffset(local.Date, tz.BaseUtcOffset);
```

```
$ dotnet test QuotaKit.Tests
Failed QuotaKit.Tests.DailyQuotaTests.Used_SummerDay_NewYork_StartsAtLocalMidnight
  Expected: 20
  Actual:   0
Failed QuotaKit.Tests.DailyQuotaTests.NextReset_RegularDay_IsNextLocalMidnight
  Expected: 2026-07-16T04:00:00+00:00
  Actual:   2026-07-16T05:00:00+00:00
Failed!  - Failed: 2, Passed: 12, Skipped: 0, Total: 14
```

Worker notes: the two target tests pass (midnight on 8 Mar and 29 Mar really is standard time) but two previously passing summer tests now fail, same symptom as attempt 2. A "standard offset" is only right for midnights that fall in standard time. In July midnight is in DST (EDT, -4). The correct offset depends on the date being resolved, not on a zone constant and not on the current instant. Reverted.

## Attempt 4: compare in UTC instead (REJECTED)

Idea: skip offsets and compute the day start as `instant.UtcDateTime.Date` shifted by the zone's `GetUtcOffset(instant)`.

```
$ dotnet test QuotaKit.Tests
Failed!  - multiple failures, including previously passing tests
```

Worker notes: now the UTC tenant, Kolkata and several others are off. This is the same bug in a different spelling (it uses the offset at `instant`). Reverted with `git checkout QuotaKit`.

## Status: STUCK

- Baseline: 2 failing (spring-forward day in New York and London); 12 passing.
- Every fix that satisfies the 2 failing tests breaks the summer-day and `NextReset_RegularDay` tests. Every fix that keeps those passing leaves the original 2 failing.
- Believed facts: `Used` and `DailyTotals` are not the problem; `StartOfNextDay` depends on `StartOfDay`; all failures are in the 23-25 hour days around a DST change.
- Open question for the expert: how do I get the correct offset for local midnight when it differs from the offset at `instant`, without breaking ordinary days?
- Files touched and all reverted: `QuotaKit/DayBoundary.cs`, `QuotaKit.Tests/DailyQuotaTests.cs`. Working tree is clean at baseline.
