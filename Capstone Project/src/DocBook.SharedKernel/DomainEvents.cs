namespace DocBook.SharedKernel;

// Something an aggregate decided. Handled inside the same module, in the same transaction.
public interface IDomainEvent;

public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
