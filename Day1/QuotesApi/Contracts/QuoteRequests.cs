using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Contracts;

public record CreateQuoteRequest(
    [property: Required]
    [property: StringLength(100)]
    string Author,

    [property: Required]
    [property: StringLength(500)]
    string Text);
public sealed record CreateCollectionRequest(
    string? Name,
    int OwnerId);

public sealed record AddCollectionItemRequest(
    int QuoteId);