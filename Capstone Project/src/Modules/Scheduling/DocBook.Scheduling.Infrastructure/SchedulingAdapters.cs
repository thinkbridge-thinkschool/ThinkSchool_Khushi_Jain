using System.Text.Json;
using DocBook.Infrastructure;
using DocBook.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Scheduling.Infrastructure;

// Publishing is writing a row: durable from the commit, sent later by the dispatcher.
public sealed class OutboxIntegrationEventPublisher(
    SchedulingDbContext context,
    IntegrationEventTypeMap types,
    TimeProvider clock) : IIntegrationEventPublisher
{
    public void Publish(IIntegrationEvent integrationEvent) =>
        context.Outbox.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = types.NameOf(integrationEvent),
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType()),
            OccurredAt = clock.GetUtcNow()
        });
}

public sealed class SchedulingOutboxStore(SchedulingDbContext context, TimeProvider clock) : IOutboxStore
{
    // READPAST is the whole point: a second instance skips rows this one holds rather than waiting.
    private const string ClaimSql = $$"""
        WITH pending AS (
            SELECT TOP ({0}) *
            FROM [{{SchedulingDbContext.Schema}}].[outbox_messages] WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE [ProcessedAt] IS NULL
              AND [AbandonedAt] IS NULL
              AND ([ClaimedUntil] IS NULL OR [ClaimedUntil] < {1})
            ORDER BY [OccurredAt]
        )
        UPDATE pending SET [ClaimedUntil] = {2}, [ClaimedBy] = {3}
        """;

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan claimDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var claimant = Guid.CreateVersion7();

        await context.Database.ExecuteSqlRawAsync(
            ClaimSql,
            [batchSize, now, now + claimDuration, claimant],
            cancellationToken);

        return await context.Outbox
            .Where(message => message.ClaimedBy == claimant)
            .OrderBy(message => message.OccurredAt)
            .ToListAsync(cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}

public sealed class SchedulingAuditTrail(SchedulingDbContext context, TimeProvider clock) : IAuditTrail
{
    public void Record(Actor actor, string action, Guid subjectId) =>
        context.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            ActorId = actor.Id,
            ActorRole = actor.Role.ToString(),
            Action = action,
            SubjectId = subjectId,
            OccurredAt = clock.GetUtcNow()
        });
}
