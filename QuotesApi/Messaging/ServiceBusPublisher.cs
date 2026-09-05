using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// Sends a drained outbox row to a Service Bus topic. The broker's MessageId is
/// the outbox row's, so a row republished after a crash arrives carrying the id
/// consumers dedupe on rather than a new one.
/// </summary>
public sealed class ServiceBusPublisher : IIntegrationEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusPublisher> _logger;

    public ServiceBusPublisher(
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusPublisher> logger)
    {
        var serviceBusOptions = options.Value;

        _client = new ServiceBusClient(serviceBusOptions.ConnectionString);
        _sender = _client.CreateSender(serviceBusOptions.Topic);
        _logger = logger;
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await _sender.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromString(message.Payload))
            {
                MessageId = message.MessageId,
                Subject = message.EventType,
                ContentType = "application/json",

                // A subscription rule can only filter on properties, not on the
                // body, so the type is carried as one as well as as the subject.
                ApplicationProperties = { ["eventType"] = message.EventType }
            },
            cancellationToken);

        _logger.LogInformation(
            "Published {MessageId} of type {EventType} to the topic.",
            message.MessageId,
            message.EventType);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
