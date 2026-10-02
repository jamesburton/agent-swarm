using QuotaKit;
using Xunit;

namespace QuotaKit.Tests;

public class DailyQuotaTests
{
    private const string Policies = """
        # tenant=limit@zone
        acme=100@America/New_York
        globex=50@UTC
        initech=80@Asia/Kolkata
        umbrella=60@Europe/London
        """;

    private static DailyQuota Make() => new(QuotaPolicy.Parse(Policies));

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ReadsAllPolicies_SkippingCommentsAndBlanks() =>
        Assert.Equal(new[] { "acme", "globex", "initech", "umbrella" }, QuotaPolicy.Parse(Policies).Select(p => p.Tenant));

    [Theory]
    [InlineData("acme=abc@UTC")]
    [InlineData("acme=-5@UTC")]
    [InlineData("acme100@UTC")]
    [InlineData("acme=5@Mars/Olympus")]
    public void Parse_RejectsMalformedLines(string line) =>
        Assert.Throws<FormatException>(() => QuotaPolicy.Parse(line));

    [Fact]
    public void Used_UtcTenant_CountsFromUtcMidnight()
    {
        var q = Make();
        q.Record("globex", Utc(2026, 5, 1, 23, 59), 7);
        q.Record("globex", Utc(2026, 5, 2, 0, 0), 3);
        q.Record("globex", Utc(2026, 5, 2, 9), 4);
        Assert.Equal(7, q.Used("globex", Utc(2026, 5, 2, 12)));
    }

    [Fact]
    public void Used_IgnoresEventsAfterNow()
    {
        var q = Make();
        q.Record("globex", Utc(2026, 5, 2, 13), 9);
        Assert.Equal(0, q.Used("globex", Utc(2026, 5, 2, 12)));
    }

    [Fact]
    public void Used_NonDstZone_UsesFixedOffset()
    {
        var q = Make();
        q.Record("initech", Utc(2026, 5, 1, 18, 29), 5); // 23:59 IST on 1 May
        q.Record("initech", Utc(2026, 5, 1, 18, 30), 6); // 00:00 IST on 2 May
        Assert.Equal(6, q.Used("initech", Utc(2026, 5, 2, 6)));
    }

    [Fact]
    public void Used_SummerDay_NewYork_StartsAtLocalMidnight()
    {
        var q = Make();
        q.Record("acme", Utc(2026, 7, 15, 3, 59), 10); // 23:59 EDT on 14 Jul
        q.Record("acme", Utc(2026, 7, 15, 4, 0), 20);  // 00:00 EDT on 15 Jul
        Assert.Equal(20, q.Used("acme", Utc(2026, 7, 15, 18)));
    }

    [Fact]
    public void Remaining_NeverNegative()
    {
        var q = Make();
        q.Record("globex", Utc(2026, 5, 2, 1), 80);
        Assert.Equal(0, q.Remaining("globex", Utc(2026, 5, 2, 2)));
    }

    [Fact]
    public void NextReset_RegularDay_IsNextLocalMidnight() =>
        Assert.Equal(Utc(2026, 7, 16, 4), Make().NextReset("acme", Utc(2026, 7, 15, 18)));

    [Fact]
    public void DailyTotals_GroupsByLocalDate()
    {
        var q = Make();
        q.Record("acme", Utc(2026, 7, 15, 3, 59), 10);
        q.Record("acme", Utc(2026, 7, 15, 4, 0), 20);
        q.Record("acme", Utc(2026, 7, 15, 20), 5);
        var totals = q.DailyTotals("acme");
        Assert.Equal(10, totals[new DateOnly(2026, 7, 14)]);
        Assert.Equal(25, totals[new DateOnly(2026, 7, 15)]);
    }

    // ---- These fail at baseline (seeded bug) ----

    [Fact]
    public void Used_SpringForwardDay_NewYork_ExcludesPreviousEvening()
    {
        // 8 Mar 2026: clocks jump 02:00 EST -> 03:00 EDT. Local midnight is still EST (UTC-5).
        var q = Make();
        q.Record("acme", Utc(2026, 3, 8, 4, 30), 10); // 23:30 EST on 7 Mar: previous day
        q.Record("acme", Utc(2026, 3, 8, 5, 30), 20); // 00:30 EST on 8 Mar: today
        Assert.Equal(20, q.Used("acme", Utc(2026, 3, 8, 19))); // 15:00 EDT
    }

    [Fact]
    public void Used_SpringForwardDay_London_ExcludesPreviousEvening()
    {
        // 29 Mar 2026: 01:00 GMT -> 02:00 BST. Local midnight is still GMT (UTC+0).
        var q = Make();
        q.Record("umbrella", Utc(2026, 3, 28, 23, 30), 4); // previous day
        q.Record("umbrella", Utc(2026, 3, 29, 0, 30), 6);  // today
        Assert.Equal(6, q.Used("umbrella", Utc(2026, 3, 29, 15)));
    }
}
