using QuotesApi.Services;

namespace QuotesApi.Models;

public sealed class Collection
{
    private readonly List<CollectionItem> _items = [];

    private Collection()
    {
    }

    public Collection(string name, int ownerId)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CollectionInvariantException("Collection name is required.");

        name = name.Trim();

        if (name.Length < 3 || name.Length > 80)
            throw new CollectionInvariantException(
                "Collection name must be between 3 and 80 characters.");

        if (ownerId <= 0)
            throw new CollectionInvariantException(
                "OwnerId must be greater than zero.");

        Name = name;
        OwnerId = ownerId;
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int OwnerId { get; private set; }

    public IReadOnlyCollection<CollectionItem> Items => _items.AsReadOnly();

    public void AddItem(int quoteId, IClock clock)
    {
        if (quoteId <= 0)
            throw new CollectionInvariantException(
                "QuoteId must be greater than zero.");

        if (_items.Count >= 50)
            throw new CollectionInvariantException(
                "A collection cannot contain more than 50 items.");

        if (_items.Any(item => item.QuoteId == quoteId))
            throw new CollectionInvariantException(
                $"QuoteId {quoteId} is already in this collection.");

        _items.Add(
            new CollectionItem(
                quoteId,
                clock.UtcNow));
    }

    public void RemoveItem(int quoteId)
    {
        var item = _items.FirstOrDefault(
            x => x.QuoteId == quoteId);

        if (item is null)
            throw new CollectionInvariantException(
                $"QuoteId {quoteId} is not in this collection.");

        _items.Remove(item);
    }
}