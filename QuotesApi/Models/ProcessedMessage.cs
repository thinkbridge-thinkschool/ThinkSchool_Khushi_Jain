namespace QuotesApi.Models;

/// <summary>
/// One delivery a consumer has already handled. The key is the subscription as
/// well as the message id, because a topic hands the same id to every
/// subscription: keying on the id alone would let whichever subscription
/// arrived first silently stop the others from running.
/// </summary>
public sealed class ProcessedMessage
{
    public const int MaximumSubscriptionLength = 100;
    public const int MaximumMessageIdLength = 200;

    private ProcessedMessage()
    {
    }

    public ProcessedMessage(string subscription, string messageId, int quoteId, DateTimeOffset handledAt)
    {
        Subscription = subscription;
        MessageId = messageId;
        QuoteId = quoteId;
        HandledAt = handledAt;
    }

    public string Subscription { get; private set; } = string.Empty;

    public string MessageId { get; private set; } = string.Empty;

    public int QuoteId { get; private set; }

    public DateTimeOffset HandledAt { get; private set; }
}
