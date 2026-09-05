using System.Text.Json;

namespace QuotesApi.Models;

/// <summary>An integration event written in the same transaction as the change that raised it, for the relay to publish later.</summary>
public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private OutboxMessage()
    {
    }

    private OutboxMessage(string messageId, string eventType, string payload, DateTimeOffset occurredAt)
    {
        MessageId = messageId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public long Id { get; private set; }

    /// <summary>Stable across a republish, so a consumer that sees it twice can recognise the second delivery.</summary>
    public string MessageId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Null until the relay has published it. The relay asks for exactly these rows.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Serialises the event here so no caller has to agree on a format separately.</summary>
    public static OutboxMessage For<TEvent>(string messageId, TEvent payload, DateTimeOffset occurredAt) =>
        new(messageId,
            typeof(TEvent).Name,
            JsonSerializer.Serialize(payload, SerializerOptions),
            occurredAt);

    public void RecordAttempt() => AttemptCount++;

    public void MarkSent(DateTimeOffset sentAt) => ProcessedAt = sentAt;
}
