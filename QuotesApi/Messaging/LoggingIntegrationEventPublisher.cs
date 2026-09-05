using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>The publisher used when no broker is configured, so a clone with no cloud account still drains its outbox.</summary>
public sealed class LoggingIntegrationEventPublisher(ILogger<LoggingIntegrationEventPublisher> logger)
    : IIntegrationEventPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Published {MessageId} of type {EventType} with payload {Payload}",
            message.MessageId,
            message.EventType,
            message.Payload);

        return Task.CompletedTask;
    }
}
