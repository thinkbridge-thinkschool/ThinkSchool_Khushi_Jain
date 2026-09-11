namespace DocBook.Infrastructure;

// The dispatcher is shared; the table belongs to the module whose schema it sits in.
public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> TakePendingAsync(int batchSize, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}
