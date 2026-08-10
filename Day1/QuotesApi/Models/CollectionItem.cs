namespace QuotesApi.Models;

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
