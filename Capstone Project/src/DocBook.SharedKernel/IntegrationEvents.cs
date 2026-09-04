namespace DocBook.SharedKernel;

// A fact one module publishes for the others. Delivered asynchronously, never in the caller's transaction.
public interface IIntegrationEvent;

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

public interface IIntegrationEventPublisher
{
    void Publish(IIntegrationEvent integrationEvent);
}
