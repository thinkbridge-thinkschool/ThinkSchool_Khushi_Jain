using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Time;

namespace QuotesApi.Messaging;

/// <summary>
/// Publishes outbox rows and marks them sent, on a poll. Publishing happens
/// before the row is marked, so a process that dies between the two leaves the
/// row unsent and the next process sends it again: delivery is at-least-once,
/// and the consumer's dedupe on MessageId is what keeps the effect single.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox relay started, waking on each write with a {BackstopSeconds}s backstop, in batches of {BatchSize}.",
            _options.PollInterval.TotalSeconds,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TryDrainAsync(stoppingToken);

            try
            {
                // A write during the drain above leaves the signal set, so the
                // next wait returns at once and nothing written is missed.
                await signal.WaitAsync(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Stop requested. No further polling cycle will start.");

        // A stop means take no new work, not abandon the queue: what is already
        // written gets one bounded chance to go out before the process ends.
        using var drainBudget = new CancellationTokenSource(_options.DrainBudget);

        await TryDrainAsync(drainBudget.Token);
        await ReportUnsentAsync();
    }

    /// <summary>One bad row must not end the relay, or every row behind it stops being delivered too.</summary>
    private async Task TryDrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            var published = await DrainAsync(cancellationToken);

            if (published > 0)
            {
                logger.LogInformation("Relay published {Published} outbox row(s).", published);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Outbox relay cycle failed. The unsent rows stay unsent and are retried on the next poll.");
        }
    }

    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        // BackgroundService is a singleton and QuotesDbContext is scoped, so each
        // cycle opens its own scope rather than capturing one context for the app's life.
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await db.Outbox
            .Where(message => message.ProcessedAt == null)
            .OrderBy(message => message.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var published = 0;

        foreach (var message in pending)
        {
            // The only place a stop is honoured. Past this point the row is
            // finished, so nothing below passes the token.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Recorded before the send, so an attempt that dies mid-flight still leaves evidence of having been tried.
            message.RecordAttempt();
            await db.SaveChangesAsync(CancellationToken.None);

            await publisher.PublishAsync(message, CancellationToken.None);

            message.MarkSent(clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);

            published++;
        }

        return published;
    }

    private async Task ReportUnsentAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var unsent = await db.Outbox.CountAsync(message => message.ProcessedAt == null, CancellationToken.None);

        if (unsent > 0)
        {
            logger.LogWarning(
                "Relay stopped with {Unsent} row(s) still unsent. They are durable and the next process will publish them.",
                unsent);

            return;
        }

        logger.LogInformation("Relay stopped with an empty outbox.");
    }
}
