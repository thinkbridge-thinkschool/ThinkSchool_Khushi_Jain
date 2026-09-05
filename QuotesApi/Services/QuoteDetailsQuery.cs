using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Data;

namespace QuotesApi.Services;

/// <summary>
/// What GET /api/quotes/{id} answers with. A record rather than the entity,
/// because a cached value has to survive being serialised and read back, and
/// the entity's setters are private so that only the aggregate can change it.
/// </summary>
public sealed record QuoteDetails(
    int Id,
    string Author,
    string Text,
    bool IsDeleted,
    string? OwnerId);

/// <summary>
/// The hot read, served through HybridCache.
///
/// GetOrCreateAsync is what provides stampede protection: on a cold key it runs
/// the loader once and hands the result to every other caller waiting on the
/// same key, so a burst of N concurrent readers costs one database read rather
/// than N.
/// </summary>
public sealed class QuoteDetailsQuery(QuotesDbContext db, HybridCache cache)
{
    public ValueTask<QuoteDetails?> RunAsync(int id, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            CacheKey(id),
            (db, id),

            // Static, so the lambda cannot close over anything scoped and keep
            // it alive past the request that created it.
            static (state, token) => state.db.Quotes
                .Where(quote => !quote.IsDeleted && quote.Id == state.id)
                .Select(quote => new QuoteDetails(
                    quote.Id,
                    quote.Author,
                    quote.Text,
                    quote.IsDeleted,
                    quote.OwnerId))
                .AsNoTracking()
                .FirstOrDefaultAsync(token)
                .AsValueTask(),

            cancellationToken: cancellationToken);

    /// <summary>
    /// Called by the write path once a quote is gone. Without it a soft-deleted
    /// quote keeps being served for the rest of the entry's lifetime.
    /// </summary>
    public ValueTask InvalidateAsync(int id, CancellationToken cancellationToken) =>
        cache.RemoveAsync(CacheKey(id), cancellationToken);

    private static string CacheKey(int id) => $"quote:{id}";
}

file static class TaskExtensions
{
    public static ValueTask<T> AsValueTask<T>(this Task<T> task) => new(task);
}
