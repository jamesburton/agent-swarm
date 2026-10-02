namespace LedgerKit;

/// <summary>Per-line sales tax by SKU. Each line is rounded to cents on its own (banker's rounding, by policy).</summary>
public sealed class TaxCalculator
{
    private readonly IReadOnlyDictionary<string, decimal> rates;
    private readonly decimal defaultRate;

    /// <summary>Initialises a new instance of the <see cref="TaxCalculator"/> class.</summary>
    /// <param name="rates">Tax rate per SKU.</param>
    /// <param name="defaultRate">Rate for SKUs not listed.</param>
    public TaxCalculator(IReadOnlyDictionary<string, decimal> rates, decimal defaultRate)
    {
        this.rates = rates;
        this.defaultRate = defaultRate;
    }

    /// <summary>Tax due on one priced line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Tax in cents precision.</returns>
    public decimal TaxFor(PricedLine line)
    {
        var rate = this.rates.TryGetValue(line.Charge.Sku, out var r) ? r : this.defaultRate;
        return Math.Round(line.Net * rate, 2);
    }
}
