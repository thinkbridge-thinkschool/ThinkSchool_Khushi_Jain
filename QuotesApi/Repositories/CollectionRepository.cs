using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class CollectionRepository(QuotesDbContext db) : ICollectionRepository
{
    /// <summary>
    /// Tracked rather than AsNoTracking: callers mutate the aggregate through
    /// its own methods and hand it back to UpdateAsync, which relies on the
    /// change tracker to work out which owned items were added or removed.
    /// </summary>
    public Task<Collection?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken) =>
        db.Collections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<Collection> AddAsync(
        Collection collection,
        CancellationToken cancellationToken)
    {
        db.Collections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public Task UpdateAsync(
        Collection collection,
        CancellationToken cancellationToken) =>
        db.SaveChangesAsync(cancellationToken);

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (collection is null)
            return false;

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
