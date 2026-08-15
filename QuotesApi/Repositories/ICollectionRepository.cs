using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);

    Task<Collection> AddAsync(
        Collection collection,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        Collection collection,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken);
}
