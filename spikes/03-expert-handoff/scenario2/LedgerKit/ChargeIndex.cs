namespace LedgerKit;

/// <summary>Read-side view of a ledger: per-account charges ordered by billing day, memoised between lookups.</summary>
public sealed class ChargeIndex
{
    private readonly ChargeLedger ledger;
    private readonly Dictionary<(string Account, int Count), IReadOnlyList<Charge>> cache = new();

    /// <summary>Initialises a new instance of the <see cref="ChargeIndex"/> class.</summary>
    /// <param name="ledger">The ledger to read from.</param>
    public ChargeIndex(ChargeLedger ledger) => this.ledger = ledger;

    /// <summary>Lookups served from the cache.</summary>
    public int Hits { get; private set; }

    /// <summary>Lookups that had to rebuild the ordered list.</summary>
    public int Misses { get; private set; }

    /// <summary>Charges for <paramref name="account"/> ordered by day; ties keep ledger arrival order.</summary>
    /// <param name="account">The account to list.</param>
    /// <returns>The ordered charges.</returns>
    public IReadOnlyList<Charge> ForAccount(string account)
    {
        var key = (account, this.ledger.CountFor(account));
        if (this.cache.TryGetValue(key, out var cached))
        {
            this.Hits++;
            return cached;
        }

        this.Misses++;
        var ordered = this.ledger.ForAccount(account).OrderBy(c => c.Day).ToList();
        this.cache[key] = ordered;
        return ordered;
    }
}
