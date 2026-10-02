namespace LedgerKit;

/// <summary>Tallies from one <see cref="BillingService.Ingest"/> call.</summary>
/// <param name="Added">Charges stored for the first time.</param>
/// <param name="Replaced">Charges corrected by a higher version.</param>
/// <param name="Ignored">Equal or older redeliveries dropped.</param>
public sealed record IngestSummary(int Added, int Replaced, int Ignored);

/// <summary>Facade: ingest charges, get statements. Default policy: 2 half-price <c>api-call</c>s, 1 half-price <c>storage-gb</c>, 10% tax except <c>support</c> at 0%.</summary>
public sealed class BillingService
{
    private readonly ChargeLedger ledger = new();
    private readonly StatementBuilder statements;

    /// <summary>Initialises a new instance of the <see cref="BillingService"/> class with the default policy.</summary>
    public BillingService()
    {
        this.Index = new ChargeIndex(this.ledger);
        this.statements = new StatementBuilder(
            this.Index,
            new DiscountEngine(new Dictionary<string, int> { ["api-call"] = 2, ["storage-gb"] = 1 }),
            new TaxCalculator(new Dictionary<string, decimal> { ["support"] = 0m }, 0.10m));
    }

    /// <summary>The read index, exposed so callers can observe cache behaviour.</summary>
    public ChargeIndex Index { get; }

    /// <summary>The underlying ledger.</summary>
    public ChargeLedger Ledger => this.ledger;

    /// <summary>Applies charges in the order given.</summary>
    /// <param name="charges">Charges to apply.</param>
    /// <returns>Tallies of what happened.</returns>
    public IngestSummary Ingest(IEnumerable<Charge> charges)
    {
        int added = 0, replaced = 0, ignored = 0;
        foreach (var c in charges)
        {
            switch (this.ledger.Apply(c))
            {
                case ApplyResult.Added: added++; break;
                case ApplyResult.Replaced: replaced++; break;
                default: ignored++; break;
            }
        }

        return new IngestSummary(added, replaced, ignored);
    }

    /// <summary>Current statement for an account.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The statement.</returns>
    public Statement Statement(string account) => this.statements.Build(account);
}
