namespace LedgerKit;

/// <summary>Outcome of <see cref="ChargeLedger.Apply"/>.</summary>
public enum ApplyResult
{
    /// <summary>The id was new and the charge was stored.</summary>
    Added,

    /// <summary>A higher version replaced the stored charge.</summary>
    Replaced,

    /// <summary>The charge was an equal or older redelivery and was dropped.</summary>
    Ignored,
}

/// <summary>Authoritative store of charges: de-duplicates by id, keeps the highest version, remembers arrival order.</summary>
public sealed class ChargeLedger
{
    private readonly Dictionary<string, Charge> byId = new(StringComparer.Ordinal);
    private readonly List<string> arrival = new();

    /// <summary>Number of distinct charge ids stored.</summary>
    public int Count => this.byId.Count;

    /// <summary>Number of stored charges currently billed to <paramref name="account"/>.</summary>
    /// <param name="account">The account to count.</param>
    /// <returns>The charge count.</returns>
    public int CountFor(string account) => this.byId.Values.Count(c => c.Account == account);

    /// <summary>Stores a charge. A replaced charge keeps its original arrival slot.</summary>
    /// <param name="charge">The charge to apply.</param>
    /// <returns>What happened to it.</returns>
    public ApplyResult Apply(Charge charge)
    {
        if (this.byId.TryGetValue(charge.Id, out var existing))
        {
            if (charge.Version <= existing.Version)
            {
                return ApplyResult.Ignored;
            }

            this.byId[charge.Id] = charge;
            return ApplyResult.Replaced;
        }

        this.byId[charge.Id] = charge;
        this.arrival.Add(charge.Id);
        return ApplyResult.Added;
    }

    /// <summary>Charges for one account in arrival order.</summary>
    /// <param name="account">The account to list.</param>
    /// <returns>A fresh list.</returns>
    public IReadOnlyList<Charge> ForAccount(string account) =>
        this.arrival.Select(id => this.byId[id]).Where(c => c.Account == account).ToList();
}
