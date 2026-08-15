namespace QuotesApi.Models;

/// <summary>
/// Aggregate root for a named set of quotes. Membership changes only through
/// <see cref="AddItem"/> and <see cref="RemoveItem"/>, which reject any change
/// that would break an invariant, so a Collection that exists is always valid.
/// </summary>
public sealed class Collection
{
    public const int MinimumNameLength = 3;
    public const int MaximumNameLength = 80;
    public const int MaximumItems = 50;

    private readonly List<CollectionItem> _items = [];

    private Collection()
    {
    }

    private Collection(string name, string ownerId)
    {
        Name = name;
        OwnerId = ownerId;
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The "sub" claim of the identity that created this collection.
    /// </summary>
    public string OwnerId { get; private set; } = string.Empty;

    /// <summary>
    /// Exposed read-only so callers cannot bypass the invariants by mutating
    /// the list directly. EF Core binds to the backing field.
    /// </summary>
    public IReadOnlyList<CollectionItem> Items => _items;

    public static Collection Create(string? name, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CollectionDomainException("Collection name is required.");

        name = name.Trim();

        if (name.Length is < MinimumNameLength or > MaximumNameLength)
            throw new CollectionDomainException(
                $"Collection name must be {MinimumNameLength}–{MaximumNameLength} characters.");

        if (string.IsNullOrWhiteSpace(ownerId))
            throw new CollectionDomainException("Collection owner is required.");

        return new Collection(name, ownerId);
    }

    /// <param name="addedAt">
    /// Supplied by the caller from IClock rather than read here, so the
    /// aggregate stays free of infrastructure and its tests need no fixture.
    /// </param>
    public void AddItem(int quoteId, DateTimeOffset addedAt)
    {
        if (_items.Count >= MaximumItems)
            throw new CollectionDomainException(
                $"A collection cannot hold more than {MaximumItems} quotes.");

        if (_items.Any(item => item.QuoteId == quoteId))
            throw new CollectionDomainException(
                $"Quote {quoteId} is already in this collection.");

        _items.Add(new CollectionItem(quoteId, addedAt));
    }

    public void RemoveItem(int quoteId)
    {
        var item = _items.FirstOrDefault(item => item.QuoteId == quoteId);

        if (item is null)
            throw new CollectionDomainException(
                $"Quote {quoteId} is not in this collection.");

        _items.Remove(item);
    }
}
