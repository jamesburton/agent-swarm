namespace LedgerKit;

/// <summary>Merges several upstream feeds into one stream for ingestion.</summary>
public static class FeedMerger
{
    /// <summary>Orders all feeds' charges by day; within a day, feed order then position in feed.</summary>
    /// <param name="feeds">Feeds in priority order.</param>
    /// <returns>The merged stream. Duplicates are NOT removed here; the ledger de-duplicates.</returns>
    public static IReadOnlyList<Charge> Merge(params IEnumerable<Charge>[] feeds) =>
        feeds.SelectMany(f => f).OrderBy(c => c.Day).ToList();
}
