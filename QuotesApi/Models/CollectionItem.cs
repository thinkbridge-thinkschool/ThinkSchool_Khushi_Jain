namespace QuotesApi.Models;

/// <summary>
/// A quote's membership in a collection. Immutable: an item is created when a
/// quote is added and discarded when it is removed, never edited in place.
/// </summary>
public sealed class CollectionItem
{
    private CollectionItem()
    {
    }

    public CollectionItem(int quoteId, DateTimeOffset addedAt)
    {
        QuoteId = quoteId;
        AddedAt = addedAt;
    }

    public int QuoteId { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }
}
