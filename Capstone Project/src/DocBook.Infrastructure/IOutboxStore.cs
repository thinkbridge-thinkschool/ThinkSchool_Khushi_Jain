namespace DocBook.Infrastructure;

// The dispatcher is shared; the table belongs to the module whose schema it sits in.
public interface IOutboxStore
{
    // Claimed rather than read, so two instances polling at once work different rows.
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan claimDuration,
        CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}
