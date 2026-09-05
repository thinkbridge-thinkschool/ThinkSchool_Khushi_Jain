using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Time;

namespace QuotesApi.Messaging;

/// <summary>
/// Consumes the quote events this API publishes. Two subscriptions receive
/// every message, and the audit one is read by several competing consumers, so
/// a message goes to exactly one of them rather than to all.
///
/// Handling is idempotent through a row keyed on the subscription and the
/// message id. Delivery is at-least-once -- the relay republishes anything it
/// could not mark sent -- so a second delivery has to be recognised rather than
/// applied twice.
/// </summary>
public sealed class QuoteEventsConsumer(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IOptions<ServiceBusOptions> options,
    ILogger<QuoteEventsConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusOptions _options = options.Value;
    private readonly List<ServiceBusProcessor> _processors = [];

    private ServiceBusClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client = new ServiceBusClient(_options.ConnectionString);

        for (var worker = 1; worker <= Math.Max(1, _options.AuditConsumers); worker++)
        {
            AddProcessor(_options.AuditSubscription, $"audit-{worker}");
        }

        AddProcessor(_options.ModerationSubscription, "moderation-1");

        foreach (var processor in _processors)
        {
            await processor.StartProcessingAsync(stoppingToken);
        }

        logger.LogInformation(
            "Quote events consumer started: {AuditConsumers} competing consumer(s) on '{Audit}', one on '{Moderation}'.",
            Math.Max(1, _options.AuditConsumers),
            _options.AuditSubscription,
            _options.ModerationSubscription);

        // The processors raise their events on their own threads, so this only
        // has to stay alive until the host asks it to stop.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop taking deliveries before anything is torn down, so a handler
        // already running is allowed to finish rather than being cut off.
        foreach (var processor in _processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        logger.LogInformation("Quote events consumer stopped.");

        await base.StopAsync(cancellationToken);
    }

    private void AddProcessor(string subscription, string worker)
    {
        var processor = _client!.CreateProcessor(
            _options.Topic,
            subscription,
            new ServiceBusProcessorOptions
            {
                // Completing explicitly is what lets a handler decide between
                // completing, abandoning for redelivery, and dead-lettering.
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1,
                PrefetchCount = 0
            });

        var workerLogger = loggerFactory.CreateLogger($"QuotesApi.Messaging.{worker}");

        processor.ProcessMessageAsync += args => HandleAsync(subscription, worker, workerLogger, args);

        processor.ProcessErrorAsync += args =>
        {
            workerLogger.LogError(
                args.Exception,
                "{Worker} failed at {Source}.",
                worker,
                args.ErrorSource);

            return Task.CompletedTask;
        };

        _processors.Add(processor);
    }

    private async Task HandleAsync(
        string subscription,
        string worker,
        ILogger workerLogger,
        ProcessMessageEventArgs args)
    {
        var message = args.Message;

        // Neither of the next two failures is transient: no number of retries
        // teaches this consumer the type or repairs the body, so they go
        // straight to the dead-letter queue instead of round-tripping forever.
        if (!string.Equals(message.Subject, nameof(QuoteCreated), StringComparison.Ordinal))
        {
            workerLogger.LogError(
                "{MessageId} carries unhandled type '{Subject}'. Dead-lettering it.",
                message.MessageId,
                message.Subject);

            await args.DeadLetterMessageAsync(
                message,
                "UnknownEventType",
                $"'{message.Subject}' is not handled by this consumer.");

            return;
        }

        QuoteCreated? published;

        try
        {
            published = message.Body.ToObjectFromJson<QuoteCreated>(SerializerOptions);
        }
        catch (JsonException exception)
        {
            workerLogger.LogError(
                "{MessageId} has an unreadable body. Dead-lettering it.",
                message.MessageId);

            await args.DeadLetterMessageAsync(message, "UnreadableBody", exception.Message);

            return;
        }

        if (published is null)
        {
            await args.DeadLetterMessageAsync(
                message,
                "UnreadableBody",
                "The body deserialized to null.");

            return;
        }

        try
        {
            await ApplyAsync(subscription, worker, workerLogger, message, published);
            await args.CompleteMessageAsync(message);
        }
        catch (Exception exception)
        {
            // Anything else is assumed transient -- a database that is down
            // comes back -- so the message is returned for redelivery rather
            // than dead-lettered on the first bad minute.
            workerLogger.LogWarning(
                exception,
                "{MessageId} delivery {Delivery} failed. Abandoning it for redelivery.",
                message.MessageId,
                message.DeliveryCount);

            await args.AbandonMessageAsync(message);
        }
    }

    private async Task ApplyAsync(
        string subscription,
        string worker,
        ILogger workerLogger,
        ServiceBusReceivedMessage message,
        QuoteCreated published)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var alreadyHandled = await db.ProcessedMessages.AnyAsync(processed =>
            processed.Subscription == subscription &&
            processed.MessageId == message.MessageId);

        if (alreadyHandled)
        {
            workerLogger.LogInformation(
                "{MessageId} was handled before on '{Subscription}', so the work is skipped.",
                message.MessageId,
                subscription);

            return;
        }

        // The record of having handled it is the work, so there is nothing to
        // keep in step with it: the row lands or it does not.
        db.ProcessedMessages.Add(new ProcessedMessage(
            subscription,
            message.MessageId,
            published.QuoteId,
            clock.UtcNow));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same id reached the key first, which
            // is the outcome this row exists to produce.
            workerLogger.LogInformation(
                "{MessageId} was handled concurrently on '{Subscription}'.",
                message.MessageId,
                subscription);

            return;
        }

        workerLogger.LogInformation(
            "{Worker} handled {MessageId} for quote {QuoteId} by {Author}.",
            worker,
            message.MessageId,
            published.QuoteId,
            published.Author);
    }
}
