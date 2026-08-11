using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Services;

public sealed class CollectionService(
    ICollectionRepository repository,
    IClock clock) : ICollectionService
{
    public async Task<Collection?> AddItemAsync(
        int collectionId,
        int quoteId,
        CancellationToken cancellationToken)
    {
        var collection = await repository.GetByIdAsync(
            collectionId,
            cancellationToken);

        if (collection is null)
            return null;

        collection.AddItem(quoteId, clock);

        await repository.UpdateAsync(
            collection,
            cancellationToken);

        return collection;
    }
}
