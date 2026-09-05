using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Time;

namespace QuotesApi.Repositories;

public class QuoteRepository(QuotesDbContext db, IClock clock, OutboxSignal outboxSignal)
    : IQuoteRepository
{
    public async Task<(IReadOnlyList<Quote> Items, int Total)> GetPagedAsync(
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var query = db.Quotes
            .Where(q => !q.IsDeleted)
            .AsNoTracking()
            .OrderBy(q => q.Id);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<Quote?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken) =>
        db.Quotes
            .Where(q => !q.IsDeleted)
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

    /// <summary>
    /// The quote and the announcement of it are one transaction, so the database
    /// and the message broker cannot disagree about whether a quote exists. A
    /// publish is never attempted here; the relay does that from the row.
    /// </summary>
    public async Task<Quote> AddAsync(
        Quote quote,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Quotes.Add(quote);
        await db.SaveChangesAsync(cancellationToken);

        // Saved second because the payload carries the identity value the first save assigned.
        db.Outbox.Add(OutboxMessage.For(
            $"quote-created-{quote.Id}",
            new QuoteCreated(quote.Id, quote.Author, quote.Text, quote.OwnerId),
            clock.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, never before: a relay woken any earlier could read
        // the table before the row is visible to it and go back to sleep.
        outboxSignal.NotifyPending();

        return quote;
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var quote = await db.Quotes
            .Where(q => !q.IsDeleted)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quote is null)
            return false;

        quote.SoftDelete();
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}