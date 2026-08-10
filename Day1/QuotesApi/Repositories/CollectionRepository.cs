using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class CollectionRepository(QuotesDbContext db) : ICollectionRepository
{
    public async Task<Collection?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await db.Collections
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Collection> AddAsync(
        Collection collection,
        CancellationToken cancellationToken)
    {
        db.Collections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task<Collection> UpdateAsync(
        Collection collection,
        CancellationToken cancellationToken)
    {
        db.Collections.Update(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection;
    }

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