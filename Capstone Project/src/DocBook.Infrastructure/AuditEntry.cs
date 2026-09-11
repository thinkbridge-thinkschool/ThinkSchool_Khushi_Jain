namespace DocBook.Infrastructure;

// Holds no detail of the change, so it can be kept far longer than the data it points at.
public sealed class AuditEntry
{
    public Guid Id { get; init; }

    public Guid ActorId { get; init; }

    public required string ActorRole { get; init; }

    public required string Action { get; init; }

    public Guid SubjectId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}
