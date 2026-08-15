using System.ComponentModel.DataAnnotations;
using QuotesApi.Models;

namespace QuotesApi.Contracts;

public record CreateCollectionRequest(
    [property: Required]
    [property: StringLength(
        Collection.MaximumNameLength,
        MinimumLength = Collection.MinimumNameLength)]
    string Name);

public record AddCollectionItemRequest(int QuoteId);
