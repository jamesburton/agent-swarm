using LedgerKit;
using Xunit;

namespace LedgerKit.Tests;

public class BillingTests
{
    private static Charge C(string id, string sku, decimal amount, int day, int version = 1, string account = "acme") =>
        new(id, account, sku, amount, day, version);

    [Fact]
    public void Statement_SingleCharge_AppliesFirstCallDiscountAndTax()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "api-call", 10.00m, 1) });
        var s = svc.Statement("acme");
        Assert.Equal(5.00m, s.Subtotal);
        Assert.Equal(0.50m, s.Tax);
        Assert.Equal(5.50m, s.Total);
    }

    [Fact]
    public void Statement_DiscountsOnlyTheFirstTwoApiCalls_InDayOrder()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a3", "api-call", 4m, 3), C("a1", "api-call", 4m, 1), C("a2", "api-call", 4m, 2) });
        var s = svc.Statement("acme");
        Assert.Equal(new[] { "a1", "a2", "a3" }, s.Lines.Select(l => l.Charge.Id));
        Assert.Equal(new[] { true, true, false }, s.Lines.Select(l => l.Discounted));
        Assert.Equal(8m, s.Subtotal);
    }

    [Fact]
    public void Statement_SameDayCharges_KeepArrivalOrder()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("z", "api-call", 2m, 5), C("m", "api-call", 2m, 5), C("a", "api-call", 2m, 5) });
        Assert.Equal(new[] { "z", "m", "a" }, svc.Statement("acme").Lines.Select(l => l.Charge.Id));
    }

    [Fact]
    public void Statement_SupportSku_IsTaxFree()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("s1", "support", 50m, 1) });
        var s = svc.Statement("acme");
        Assert.Equal(0m, s.Tax);
        Assert.Equal(50m, s.Total);
    }

    [Fact]
    public void HalfPrice_RoundsToEven_ByPolicy()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "api-call", 10.01m, 1) });
        Assert.Equal(5.00m, svc.Statement("acme").Lines[0].Net);
    }

    [Fact]
    public void Tax_IsRoundedPerLine_NotOnTheSum()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("x", "misc", 0.25m, 1), C("y", "misc", 0.25m, 1) });
        Assert.Equal(0.04m, svc.Statement("acme").Tax);
    }

    [Fact]
    public void Ingest_NewCharge_ChangesStatement()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        Assert.Equal(11.00m, svc.Statement("acme").Total);
        svc.Ingest(new[] { C("a2", "misc", 10m, 2) });
        Assert.Equal(22.00m, svc.Statement("acme").Total);
    }

    [Fact]
    public void Ingest_OlderOrEqualVersion_IsIgnored()
    {
        var svc = new BillingService();
        var summary = svc.Ingest(new[]
        {
            C("a1", "misc", 10m, 1, version: 2),
            C("a1", "misc", 99m, 1, version: 1),
            C("a1", "misc", 77m, 1, version: 2),
        });
        Assert.Equal(new IngestSummary(1, 0, 2), summary);
        Assert.Equal(11.00m, svc.Statement("acme").Total);
    }

    [Fact]
    public void Ingest_HigherVersion_ReportsReplaced()
    {
        var svc = new BillingService();
        Assert.Equal(new IngestSummary(1, 0, 0), svc.Ingest(new[] { C("a1", "misc", 10m, 1) }));
        Assert.Equal(new IngestSummary(0, 1, 0), svc.Ingest(new[] { C("a1", "misc", 12m, 1, version: 2) }));
        Assert.Equal(1, svc.Ledger.Count);
    }

    [Fact]
    public void Statements_AreIsolatedPerAccount()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1), C("b1", "misc", 20m, 1, account: "globex") });
        Assert.Equal(11.00m, svc.Statement("acme").Total);
        Assert.Equal(22.00m, svc.Statement("globex").Total);
        Assert.Empty(svc.Statement("nobody").Lines);
    }

    [Fact]
    public void FeedMerger_OrdersByDay_ThenFeedOrder()
    {
        var f1 = new[] { C("p", "misc", 1m, 2), C("q", "misc", 1m, 1) };
        var f2 = new[] { C("r", "misc", 1m, 1), C("s", "misc", 1m, 2) };
        Assert.Equal(new[] { "q", "r", "p", "s" }, FeedMerger.Merge(f1, f2).Select(c => c.Id));
    }

    [Fact]
    public void Render_IsStableAndCultureIndependent()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "api-call", 10m, 1) });
        Assert.Equal(
            "Statement for acme\n  d1 a1 api-call 5.00 *\nSubtotal 5.00 Tax 0.50 Total 5.50\n",
            svc.Statement("acme").Render());
    }

    // ---- caching behaviour of ChargeIndex ----

    [Fact]
    public void Index_SecondStatementWithoutChanges_HitsCache()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        svc.Statement("acme");
        svc.Statement("acme");
        Assert.Equal(1, svc.Index.Misses);
        Assert.Equal(1, svc.Index.Hits);
    }

    [Fact]
    public void Index_RedeliveredDuplicate_KeepsCacheWarm()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        svc.Statement("acme");
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        svc.Statement("acme");
        Assert.Equal(1, svc.Index.Misses);
        Assert.Equal(1, svc.Index.Hits);
    }

    [Fact]
    public void Index_AddingAChargeToAnotherAccount_DoesNotChangeThisStatement()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        var before = svc.Statement("acme");
        svc.Ingest(new[] { C("b1", "misc", 5m, 1, account: "globex") });
        Assert.Equal(before.Total, svc.Statement("acme").Total);
    }

    // ---- corrections (a higher version of an existing charge) ----

    [Fact]
    public void Correction_WithAnAdditionalCharge_IsReflected()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1) });
        svc.Statement("acme");
        svc.Ingest(new[] { C("a1", "misc", 12m, 1, version: 2), C("a2", "misc", 1m, 2) });
        Assert.Equal(14.30m, svc.Statement("acme").Total);
    }

    [Fact]
    public void Correction_ChangesAmount_StatementTotalFollows()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "api-call", 10.00m, 1) });
        Assert.Equal(5.50m, svc.Statement("acme").Total);
        svc.Ingest(new[] { C("a1", "api-call", 12.00m, 1, version: 2) });
        Assert.Equal(6.60m, svc.Statement("acme").Total);
    }

    [Fact]
    public void Correction_ChangesAmount_LineNetFollows()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "misc", 10m, 1), C("a2", "misc", 20m, 2) });
        svc.Statement("acme");
        svc.Ingest(new[] { C("a2", "misc", 30m, 2, version: 2) });
        Assert.Equal(new[] { 10m, 30m }, svc.Statement("acme").Lines.Select(l => l.Net));
    }

    [Fact]
    public void Correction_ThenStatement_MatchesAFreshServiceWithTheSameFinalCharges()
    {
        var svc = new BillingService();
        svc.Ingest(new[] { C("a1", "storage-gb", 8m, 1), C("a2", "storage-gb", 6m, 2) });
        svc.Statement("acme");
        svc.Ingest(new[] { C("a2", "storage-gb", 9m, 2, version: 2) });

        var fresh = new BillingService();
        fresh.Ingest(new[] { C("a1", "storage-gb", 8m, 1), C("a2", "storage-gb", 9m, 2, version: 2) });
        Assert.Equal(fresh.Statement("acme").Render(), svc.Statement("acme").Render());
    }
}
