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

public sealed class SchedulingOutboxStore(SchedulingDbContext context) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessage>> TakePendingAsync(
        int batchSize,
        CancellationToken cancellationToken) =>
        await context.Outbox
            .Where(message => message.ProcessedAt == null)
            .OrderBy(message => message.OccurredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

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
