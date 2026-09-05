using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>Where a drained outbox row goes. Behind an interface so the API runs with no broker configured.</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
