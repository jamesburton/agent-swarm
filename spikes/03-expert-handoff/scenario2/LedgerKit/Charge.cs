namespace LedgerKit;

/// <summary>A billable charge as delivered by an upstream feed. A later <see cref="Version"/> with the same <see cref="Id"/> corrects the earlier one.</summary>
/// <param name="Id">Globally unique charge id, compared ordinally.</param>
/// <param name="Account">Account the charge is billed to.</param>
/// <param name="Sku">Product code, e.g. <c>api-call</c> or <c>storage-gb</c>.</param>
/// <param name="Amount">Gross amount before discounts and tax.</param>
/// <param name="Day">Billing day number (1-based) the charge belongs to.</param>
/// <param name="Version">Revision of this charge; starts at 1.</param>
public sealed record Charge(string Id, string Account, string Sku, decimal Amount, int Day, int Version = 1);
