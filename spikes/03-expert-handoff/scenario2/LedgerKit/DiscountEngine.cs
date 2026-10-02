namespace LedgerKit;

/// <summary>A charge after discounting.</summary>
/// <param name="Charge">The source charge.</param>
/// <param name="Net">Amount after discount, rounded to cents (banker's rounding, by policy).</param>
/// <param name="Discounted">Whether a discount was applied.</param>
public sealed record PricedLine(Charge Charge, decimal Net, bool Discounted);

/// <summary>Applies "first N charges of a SKU are half price" rules, walking charges in the order given.</summary>
public sealed class DiscountEngine
{
    private readonly IReadOnlyDictionary<string, int> halfPriceFirst;

    /// <summary>Initialises a new instance of the <see cref="DiscountEngine"/> class.</summary>
    /// <param name="halfPriceFirst">For each SKU, how many leading charges are half price.</param>
    public DiscountEngine(IReadOnlyDictionary<string, int> halfPriceFirst) => this.halfPriceFirst = halfPriceFirst;

    /// <summary>Prices an ordered charge list.</summary>
    /// <param name="ordered">Charges in billing order.</param>
    /// <returns>One priced line per charge, same order.</returns>
    public IReadOnlyList<PricedLine> Apply(IReadOnlyList<Charge> ordered)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<PricedLine>(ordered.Count);
        foreach (var charge in ordered)
        {
            seen.TryGetValue(charge.Sku, out var n);
            seen[charge.Sku] = n + 1;
            var allowed = this.halfPriceFirst.TryGetValue(charge.Sku, out var limit) ? limit : 0;
            lines.Add(n < allowed
                ? new PricedLine(charge, Math.Round(charge.Amount * 0.5m, 2), true)
                : new PricedLine(charge, Math.Round(charge.Amount, 2), false));
        }

        return lines;
    }
}
