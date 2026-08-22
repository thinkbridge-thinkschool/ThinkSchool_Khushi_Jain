using QuotesApi.Repositories;
using QuotesApi.Time;

namespace QuotesApi.Services;

public sealed class AddQuoteToCollectionHandler(
    ICollectionRepository repository,
    IClock clock)
{
    // False when no collection has that id. Invariant breaches throw.
    public async Task<bool> HandleAsync(
        int collectionId,
        int quoteId,
        CancellationToken cancellationToken)
    {
        var collection = await repository.GetByIdAsync(collectionId, cancellationToken);

        if (collection is null)
        {
            return false;
        }

        collection.AddItem(quoteId, clock.UtcNow);

        await repository.UpdateAsync(collection, cancellationToken);

        return true;
    }
}
