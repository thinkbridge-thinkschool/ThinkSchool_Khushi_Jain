namespace DocBook.Infrastructure;

// One pending integration event. Written in the same transaction as the change that caused it.
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    // Separate from ProcessedAt, or a message that never sent would read as one that did.
    public DateTimeOffset? AbandonedAt { get; set; }

    public int Attempts { get; set; }

    // The failure's type, never its text, which is free to quote the payload it choked on.
    public string? LastFailure { get; set; }

    // A lease rather than a lock, so an instance that dies mid-delivery strands nothing.
    public DateTimeOffset? ClaimedUntil { get; set; }

    public Guid? ClaimedBy { get; set; }
}
