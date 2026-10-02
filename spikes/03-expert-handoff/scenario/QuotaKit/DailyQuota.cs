namespace QuotaKit;

/// <summary>Tracks per-tenant usage and answers "how much is left today?" in each tenant's local day.</summary>
public sealed class DailyQuota
{
    private readonly Dictionary<string, QuotaPolicy> policies;
    private readonly Dictionary<string, List<(DateTimeOffset At, long Units)>> events = new();

    /// <summary>Creates a tracker for the given policies.</summary>
    /// <param name="policies">One policy per tenant; tenant ids must be unique.</param>
    public DailyQuota(IEnumerable<QuotaPolicy> policies) =>
        this.policies = policies.ToDictionary(p => p.Tenant);

    /// <summary>Records consumption.</summary>
    /// <param name="tenant">Tenant id.</param>
    /// <param name="at">When the units were consumed.</param>
    /// <param name="units">Units consumed (positive).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is not positive.</exception>
    public void Record(string tenant, DateTimeOffset at, long units)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        Policy(tenant);
        if (!events.TryGetValue(tenant, out var list))
        {
            events[tenant] = list = new();
        }

        list.Add((at, units));
    }

    /// <summary>Units consumed during the tenant's local day containing <paramref name="now"/>, up to and including <paramref name="now"/>.</summary>
    /// <param name="tenant">Tenant id.</param>
    /// <param name="now">The evaluation instant.</param>
    /// <returns>Total units in [start of local day, now].</returns>
    public long Used(string tenant, DateTimeOffset now)
    {
        var start = DayBoundary.StartOfDay(now, Policy(tenant).Zone);
        return events.TryGetValue(tenant, out var list)
            ? list.Where(e => e.At >= start && e.At <= now).Sum(e => e.Units)
            : 0;
    }

    /// <summary>Units still available today (never negative).</summary>
    /// <param name="tenant">Tenant id.</param>
    /// <param name="now">The evaluation instant.</param>
    /// <returns>Limit minus used, floored at zero.</returns>
    public long Remaining(string tenant, DateTimeOffset now) =>
        Math.Max(0, Policy(tenant).Limit - Used(tenant, now));

    /// <summary>When the quota next resets for this tenant.</summary>
    /// <param name="tenant">Tenant id.</param>
    /// <param name="now">The evaluation instant.</param>
    /// <returns>The start of the next local day.</returns>
    public DateTimeOffset NextReset(string tenant, DateTimeOffset now) =>
        DayBoundary.StartOfNextDay(now, Policy(tenant).Zone);

    /// <summary>Usage per local calendar date for one tenant, in date order.</summary>
    /// <param name="tenant">Tenant id.</param>
    /// <returns>Date to total units.</returns>
    public IReadOnlyDictionary<DateOnly, long> DailyTotals(string tenant)
    {
        var zone = Policy(tenant).Zone;
        var totals = new SortedDictionary<DateOnly, long>();
        foreach (var (at, units) in events.GetValueOrDefault(tenant) ?? new())
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
            totals[date] = totals.GetValueOrDefault(date) + units;
        }

        return totals;
    }

    private QuotaPolicy Policy(string tenant) =>
        policies.TryGetValue(tenant, out var p) ? p : throw new KeyNotFoundException($"Unknown tenant '{tenant}'");
}
