namespace QuotaKit;

/// <summary>Calendar-day arithmetic for tenants whose "day" is defined by their own time zone.</summary>
public static class DayBoundary
{
    /// <summary>Returns the instant at which the tenant-local calendar day containing <paramref name="instant"/> began.</summary>
    /// <param name="instant">Any instant within the day.</param>
    /// <param name="tz">The tenant's time zone.</param>
    /// <returns>The first instant of that local day.</returns>
    public static DateTimeOffset StartOfDay(DateTimeOffset instant, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(instant, tz);

        // Local midnight of the same calendar date, re-anchored with the zone's offset.
        return new DateTimeOffset(local.Date, local.Offset);
    }

    /// <summary>Returns the instant at which the tenant-local day after the one containing <paramref name="instant"/> begins.</summary>
    /// <param name="instant">Any instant within the current day.</param>
    /// <param name="tz">The tenant's time zone.</param>
    /// <returns>The first instant of the next local day.</returns>
    public static DateTimeOffset StartOfNextDay(DateTimeOffset instant, TimeZoneInfo tz)
    {
        // A local day is 23-25 hours long, so 36 hours after its start is always inside the next day.
        var probe = StartOfDay(instant, tz).AddHours(36);
        return StartOfDay(probe, tz);
    }

    /// <summary>Returns the true length of the local day containing <paramref name="instant"/> (23h, 24h or 25h on DST days).</summary>
    /// <param name="instant">Any instant within the day.</param>
    /// <param name="tz">The tenant's time zone.</param>
    /// <returns>The elapsed duration between the day's start and the next day's start.</returns>
    public static TimeSpan DayLength(DateTimeOffset instant, TimeZoneInfo tz) =>
        StartOfNextDay(instant, tz) - StartOfDay(instant, tz);
}
