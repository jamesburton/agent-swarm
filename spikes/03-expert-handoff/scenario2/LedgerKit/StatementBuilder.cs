using System.Globalization;
using System.Text;

namespace LedgerKit;

/// <summary>A rendered account statement.</summary>
/// <param name="Account">Account the statement is for.</param>
/// <param name="Lines">Priced lines in billing order.</param>
/// <param name="Subtotal">Sum of net amounts.</param>
/// <param name="Tax">Sum of per-line tax.</param>
public sealed record Statement(string Account, IReadOnlyList<PricedLine> Lines, decimal Subtotal, decimal Tax)
{
    /// <summary>Subtotal plus tax.</summary>
    public decimal Total => this.Subtotal + this.Tax;

    /// <summary>Plain-text rendering, culture independent.</summary>
    /// <returns>Multi-line text.</returns>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Statement for {this.Account}\n");
        foreach (var l in this.Lines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  d{l.Charge.Day} {l.Charge.Id} {l.Charge.Sku} {l.Net:F2}{(l.Discounted ? " *" : string.Empty)}\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"Subtotal {this.Subtotal:F2} Tax {this.Tax:F2} Total {this.Total:F2}\n");
        return sb.ToString();
    }
}

/// <summary>Composes index, discounts and tax into a statement.</summary>
public sealed class StatementBuilder
{
    private readonly ChargeIndex index;
    private readonly DiscountEngine discounts;
    private readonly TaxCalculator tax;

    /// <summary>Initialises a new instance of the <see cref="StatementBuilder"/> class.</summary>
    /// <param name="index">Where charges come from.</param>
    /// <param name="discounts">Discount rules.</param>
    /// <param name="tax">Tax rules.</param>
    public StatementBuilder(ChargeIndex index, DiscountEngine discounts, TaxCalculator tax)
    {
        this.index = index;
        this.discounts = discounts;
        this.tax = tax;
    }

    /// <summary>Builds the current statement for an account.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The statement.</returns>
    public Statement Build(string account)
    {
        var lines = this.discounts.Apply(this.index.ForAccount(account));
        return new Statement(account, lines, lines.Sum(l => l.Net), lines.Sum(this.tax.TaxFor));
    }
}
