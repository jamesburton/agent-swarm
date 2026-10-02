# Expert hand-off

## Goal
`dotnet test` in `spikes/03-expert-handoff/scenario/QuotaKit.Tests` has 2 failing tests. Fix the library so all tests pass. Do not delete tests.

## Current state
`dotnet test` on visible tests: 2 failed, 12 passed, 14 total.
Run: `dotnet test QuotaKit.Tests` from the scenario root.
Failing tests:
- `QuotaKit.Tests.DailyQuotaTests.Used_SpringForwardDay_London_ExcludesPreviousEvening`: Assert.Equal() Failure: Values differ | Expected: 6 | Actual:   10
- `QuotaKit.Tests.DailyQuotaTests.Used_SpringForwardDay_NewYork_ExcludesPreviousEvening`: Assert.Equal() Failure: Values differ | Expected: 20 | Actual:   30

## Files that matter
- `QuotaKit.Tests/DailyQuotaTests.cs`: xUnit tests; the 2 failing ones are under the `seeded bug` comment; do not delete tests
- `QuotaKit/DailyQuota.cs`: Tracks per-tenant usage and answers "how much is left today?" in each tenant's local day.
- `QuotaKit/DayBoundary.cs`: Calendar-day arithmetic for tenants whose "day" is defined by their own time zone.
- `QuotaKit/QuotaPolicy.cs`: A tenant's daily allowance and the time zone that defines its day.

## Failed attempts
1. **change the test expectation** [REJECTED by worker]
   - Tried: the event at 04:30Z is only 30 minutes before the "start", maybe the test data is off.
   - Why it failed: London test still fails (expects 6, gets 10). Also this contradicts the task: 23:30 EST on 7 Mar is plainly the previous local day, so a quota of 30 for 8 Mar is wrong. Reverted with `git checkout QuotaKit.Tests`.
2. **special-case DST in StartOfDay** [REJECTED: breaks other tests]
   - Tried: on DST days `local.Offset` is one hour ahead of midnight's offset, so add one hour. `var start = new DateTimeOffset(local.Date, local.Offset); if (tz.IsDaylightSavingTime(instant)) start = start.AddHours(1); return start;`
   - Why it failed: the spring-forward tests now pass, but the shift applies to every summer day, not just the transition day. Plain July days are now one hour late. `IsDaylightSavingTime` is true all summer; I need "is this the transition day", which I cannot get cheaply. Reverted.
   - Evidence: Failed QuotaKit.Tests.DailyQuotaTests.Used_SummerDay_NewYork_StartsAtLocalMidnight; Expected: 20; Actual:   0; Failed QuotaKit.Tests.DailyQuotaTests.NextReset_RegularDay_IsNextLocalMidnight
3. **use the zone's standard (base) offset** [TEMPTING WRONG FIX, REJECTED]
   - Tried: midnight on the spring-forward day is in standard time, so anchor to `tz.BaseUtcOffset`, which is -5 for New York and 0 for London. `return new DateTimeOffset(local.Date, tz.BaseUtcOffset);`
   - Why it failed: the two target tests pass (midnight on 8 Mar and 29 Mar really is standard time) but two previously passing summer tests now fail, same symptom as attempt 2. A "standard offset" is only right for midnights that fall in standard time. In July midnight is in DST (EDT, -4). The correct offset depends on the date being resolved, not on a zone constant and not on the current instant. Reverted.
   - Evidence: Failed QuotaKit.Tests.DailyQuotaTests.Used_SummerDay_NewYork_StartsAtLocalMidnight; Expected: 20; Actual:   0; Failed QuotaKit.Tests.DailyQuotaTests.NextReset_RegularDay_IsNextLocalMidnight
4. **compare in UTC instead** [REJECTED]
   - Tried: skip offsets and compute the day start as `instant.UtcDateTime.Date` shifted by the zone's `GetUtcOffset(instant)`.
   - Why it failed: now the UTC tenant, Kolkata and several others are off. This is the same bug in a different spelling (it uses the offset at `instant`). Reverted with `git checkout QuotaKit`.

## Rejected hypotheses
- Test data/expectations are wrong (attempt 1): no, the events are on the previous local day; do not edit tests.
- Fix by testing whether the instant is in DST, or by using a zone-constant offset (attempts 2, 3): no, the right offset belongs to the midnight being resolved.
- Fix by doing the arithmetic in UTC with the offset at `instant` (attempt 4): no, same bug in different spelling.
- Worker's belief (unverified): `Used` and `DailyTotals` are not the problem; `StartOfNextDay` depends on `StartOfDay`; all failures are in the 23-25 hour days around a DST change.

## Open question
how do I get the correct offset for local midnight when it differs from the offset at `instant`, without breaking ordinary days?

## Constraints
- Task: "`dotnet test` in `spikes/03-expert-handoff/scenario/QuotaKit.Tests` has 2 failing tests. Fix the library so all tests pass. Do not delete tests."
- Do not delete or weaken tests; do not change public signatures.
- Working tree is clean at baseline (earlier edits reverted: `QuotaKit/DayBoundary.cs`, `QuotaKit.Tests/DailyQuotaTests.cs`).
- Do NOT re-try any approach under Failed attempts. If you think one is right, first explain why it does not break the passing tests.

## Expected result shape
- All visible tests pass (14/14); changes limited to library code, not tests.
- Final reply: root cause in one paragraph, a diff summary, and the `dotnet test` summary line.
