namespace QuotesApi.Messaging;

/// <summary>What other systems are told when a quote is created. A published contract, not the entity.</summary>
public sealed record QuoteCreated(int QuoteId, string Author, string Text, string? OwnerId);
