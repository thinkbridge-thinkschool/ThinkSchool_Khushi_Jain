using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.Services;

// Read model for the collection detail screen: quote author and text, which the
// aggregate never holds, flattened onto each membership row.
public sealed record CollectionDetails(
    int Id,
    string Name,
    string OwnerId,
    int ItemCount,
    IReadOnlyList<CollectionDetailsItem> Items);

public sealed record CollectionDetailsItem(
    int QuoteId,
    string Author,
    string Text,
    DateTimeOffset AddedAt);

public sealed class CollectionDetailsQuery(QuotesDbContext db)
{
    // One statement, nothing entity-shaped, so nothing is tracked.
    public Task<CollectionDetails?> RunAsync(
        int id,
        CancellationToken cancellationToken) =>
        db.Collections
            .Where(collection => collection.Id == id)
            .Select(collection => new CollectionDetails(
                collection.Id,
                collection.Name,
                collection.OwnerId,
                collection.Items.Count,
                (from item in collection.Items
                 join quote in db.Quotes on item.QuoteId equals quote.Id
                 where !quote.IsDeleted
                 // SQLite cannot ORDER BY a DateTimeOffset; the owned key is add order.
                 orderby EF.Property<int>(item, "Id")
                 select new CollectionDetailsItem(
                     quote.Id,
                     quote.Author,
                     quote.Text,
                     item.AddedAt))
                .ToList()))
            .FirstOrDefaultAsync(cancellationToken);
}
