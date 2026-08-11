using QuotesApi.Models;

namespace QuotesApi.Services;

public interface ICollectionService
{
    Task<Collection?> AddItemAsync(
        int collectionId,
        int quoteId,
        CancellationToken cancellationToken);
}