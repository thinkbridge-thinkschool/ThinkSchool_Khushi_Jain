using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

const string Topic = "quote-events";

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
}));

var log = loggerFactory.CreateLogger("day19");

await using var client = new ServiceBusClient(
    Environment.GetEnvironmentVariable("SERVICE_BUS_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set SERVICE_BUS_CONNECTION_STRING. See day19_service_bus/README.md."));

var created = BinaryData.FromObjectAsJson(new QuoteEvent("QuoteCreated", 1001, "Ada Lovelace", "The Analytical Engine weaves algebraic patterns."), json);
var archived = BinaryData.FromObjectAsJson(new QuoteEvent("QuoteArchived", 1002, "Grace Hopper", "A ship in port is safe, but that is not what ships are built for."), json);
var doomed = BinaryData.FromObjectAsJson(new QuoteEvent("QuoteCreated", 1004, "Edsger Dijkstra", "Simplicity is a prerequisite for reliability."), json);

var failsDownstream = Event("evt-1004", "QuoteCreated", doomed);
failsDownstream.ApplicationProperties["failAuditWrites"] = true;

var outbox = new[]
{
    Event("evt-1001", "QuoteCreated", created),
    Event("evt-1001", "QuoteCreated", created),
    Event("evt-1002", "QuoteArchived", archived),
    Event("evt-1003", "QuoteCreated", BinaryData.FromString("""{"eventType":"QuoteCreated","quoteId":"not-a-number"}""")),
    failsDownstream,
};

await using (var sender = client.CreateSender(Topic))
{
    foreach (var message in outbox)
    {
        await sender.SendMessageAsync(message);
        log.LogInformation("Published {MessageId} ({Subject}).", message.MessageId, message.Subject);
    }
}

var handled = new ConcurrentDictionary<(string Subscription, string MessageId), bool>();

var workers = new[]
{
    Worker("audit", "audit-1", Audit),
    Worker("audit", "audit-2", Audit),
    Worker("moderation", "moderation-1", Moderate),
};

foreach (var worker in workers)
{
    await worker.StartProcessingAsync();
}

log.LogInformation("Two competing consumers on 'audit', one on 'moderation'.");
await Task.Delay(TimeSpan.FromSeconds(15));

foreach (var worker in workers)
{
    await worker.DisposeAsync();
}

log.LogInformation(
    "Handled exactly once: {Audit} on 'audit', {Moderation} on 'moderation'.",
    handled.Keys.Count(key => key.Subscription == "audit"),
    handled.Keys.Count(key => key.Subscription == "moderation"));

await ReportDeadLetters("audit");
await ReportDeadLetters("moderation");

ServiceBusMessage Event(string messageId, string eventType, BinaryData body) => new(body)
{
    MessageId = messageId,
    Subject = eventType,
    ContentType = "application/json",
    ApplicationProperties = { ["eventType"] = eventType },
};

string? Audit(ServiceBusReceivedMessage message, ILogger logger)
{
    QuoteEvent? quote;

    try
    {
        quote = message.Body.ToObjectFromJson<QuoteEvent>(json);
    }
    catch (JsonException ex)
    {
        return ex.Message;
    }

    if (quote is null)
    {
        return "The body deserialized to null.";
    }

    if (message.ApplicationProperties.TryGetValue("failAuditWrites", out var flag) && flag is true)
    {
        throw new InvalidOperationException("The audit ledger is unavailable.");
    }

    logger.LogInformation("Audited {EventType} for quote {QuoteId} by {Author}.", quote.EventType, quote.QuoteId, quote.Author);

    return null;
}

string? Moderate(ServiceBusReceivedMessage message, ILogger logger)
{
    logger.LogInformation("Queued {Subject} ({MessageId}) for moderation.", message.Subject, message.MessageId);

    return null;
}

ServiceBusProcessor Worker(string subscription, string name, Func<ServiceBusReceivedMessage, ILogger, string?> handle)
{
    var logger = loggerFactory.CreateLogger(name);

    var processor = client.CreateProcessor(Topic, subscription, new ServiceBusProcessorOptions
    {
        MaxConcurrentCalls = 1,
        AutoCompleteMessages = false,
        PrefetchCount = 0,
    });

    processor.ProcessMessageAsync += async args =>
    {
        var message = args.Message;
        var key = (subscription, message.MessageId);

        if (!handled.TryAdd(key, true))
        {
            logger.LogInformation("{MessageId}: '{Subscription}' has this id already, skipping the work.", message.MessageId, subscription);
            await args.CompleteMessageAsync(message);

            return;
        }

        try
        {
            var poison = handle(message, logger);

            if (poison is null)
            {
                await args.CompleteMessageAsync(message);

                return;
            }

            handled.TryRemove(key, out _);
            logger.LogError("{MessageId}: {Reason} Dead-lettering without retrying.", message.MessageId, poison);
            await args.DeadLetterMessageAsync(message, "HandlerRejected", poison);
        }
        catch (Exception ex)
        {
            handled.TryRemove(key, out _);
            logger.LogWarning("{MessageId} delivery {Delivery}: {Error} Abandoning for redelivery.", message.MessageId, message.DeliveryCount, ex.Message);
            await args.AbandonMessageAsync(message);
        }
    };

    processor.ProcessErrorAsync += args =>
    {
        logger.LogError("{Name} failed at {Source}: {Error}", name, args.ErrorSource, args.Exception.Message);

        return Task.CompletedTask;
    };

    return processor;
}

async Task ReportDeadLetters(string subscription)
{
    await using var receiver = client.CreateReceiver(
        Topic,
        subscription,
        new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    var messages = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(5));

    log.LogInformation("'{Subscription}' dead-letter queue holds {Count} message(s).", subscription, messages.Count);

    foreach (var message in messages)
    {
        log.LogInformation(
            "  {MessageId} after {Delivery} delivery(s) | {Reason}: {Description} | {Body}",
            message.MessageId,
            message.DeliveryCount,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.Body.ToString());
    }
}

public sealed record QuoteEvent(string EventType, int QuoteId, string Author, string Text);
