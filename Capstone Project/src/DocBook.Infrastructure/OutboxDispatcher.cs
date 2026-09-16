using System.Text.Json;
using DocBook.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocBook.Infrastructure;

public sealed class OutboxOptions
{
    public int BatchSize { get; init; } = 50;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxAttempts { get; init; } = 5;

    // Long enough for a slow delivery, short enough that a killed instance's work resumes soon.
    public TimeSpan ClaimDuration { get; init; } = TimeSpan.FromMinutes(2);
}

// Delivery is at least once. The claim keeps two instances off one row; the handled-message
// table in Notifications is what makes the redelivery this still allows harmless.
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IntegrationEventTypeMap types,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The outbox dispatcher failed a pass.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var pending = await store.ClaimPendingAsync(
            _options.BatchSize,
            _options.ClaimDuration,
            cancellationToken);

        foreach (var message in pending)
        {
            message.Attempts++;

            try
            {
                await DeliverAsync(scope.ServiceProvider, message, cancellationToken);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastFailure = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type, not the text, which a handler is free to build from the payload.
                message.LastFailure = exception.GetType().Name;

                // The claim is left to expire, which is where the wait before a retry comes from.

                logger.LogError(
                    "Outbox message {MessageId} failed on attempt {Attempt} with {Failure}.",
                    message.Id,
                    message.Attempts,
                    message.LastFailure);

                if (message.Attempts >= _options.MaxAttempts)
                {
                    message.ProcessedAt = DateTimeOffset.UtcNow;

                    logger.LogError(
                        "Outbox message {MessageId} abandoned after {Attempts} attempts.",
                        message.Id,
                        message.Attempts);
                }
            }
        }

        await store.SaveAsync(cancellationToken);
    }

    private async Task DeliverAsync(
        IServiceProvider scoped,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (types.Find(message.Type) is not { } eventType)
        {
            throw new InvalidOperationException($"No integration event is registered as '{message.Type}'.");
        }

        if (JsonSerializer.Deserialize(message.Payload, eventType) is not IIntegrationEvent integrationEvent)
        {
            throw new InvalidOperationException($"Outbox message {message.Id} did not deserialize.");
        }

        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var handle = handlerType.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;

        foreach (var handler in scoped.GetServices(handlerType))
        {
            await (Task)handle.Invoke(handler, [integrationEvent, cancellationToken])!;
        }
    }
}
