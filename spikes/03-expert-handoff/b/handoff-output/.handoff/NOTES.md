# Worker notes (escalation hand-off)

Task: `dotnet test` in `QuotaKit.Tests` has 2 failing tests; fix the library so all pass; do not delete tests.
Status: STUCK. Working tree is clean at baseline (all attempts reverted). Raw test output: `last-test-run.txt`. Code of attempts: `attempts.diff`.

## Believed facts
- Both failing tests are on spring-forward dates; `Used` and `DailyTotals` are not the problem; `StartOfNextDay` depends on `StartOfDay`. Evidence: worker-transcript.md:5-20, :90-96
- Every fix that satisfies the 2 failing tests breaks the summer-day and `NextReset_RegularDay` tests, and vice versa.

## Failed attempts (do NOT repeat)
- Attempt 1 (change the test expectation): the event at 04:30Z is only 30 minutes before the "start", maybe the test data is off. Failed: London test still fails (expects 6, gets 10). Evidence: worker-transcript.md:21-34
- Attempt 2 (special-case DST in StartOfDay): on DST days `local.Offset` is one hour ahead of midnight's offset, so add one hour. Failed: Plain July days are now one hour late. Evidence: worker-transcript.md:35-57
- Attempt 3 (use the zone's standard (base) offset) [TEMPTING WRONG FIX]: midnight on the spring-forward day is in standard time, so anchor to `tz.BaseUtcOffset`, which is -5 for New York and 0 for London. Failed: the two target tests pass (midnight on 8 Mar and 29 Mar really is standard time) but two previously passing summer tests now fail, same symptom as attempt 2. Evidence: worker-transcript.md:58-78
- Attempt 4 (compare in UTC instead): skip offsets and compute the day start as `instant.UtcDateTime.Date` shifted by the zone's `GetUtcOffset(instant)`. Failed: now the UTC tenant, Kolkata and several others are off. Evidence: worker-transcript.md:79-89

## Open question
- How do I get the correct offset for local midnight when it differs from the offset at `instant`, without breaking ordinary days? Evidence: worker-transcript.md:90-96
