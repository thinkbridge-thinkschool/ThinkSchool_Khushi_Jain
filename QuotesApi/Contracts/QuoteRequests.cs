using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Contracts;

public record CreateQuoteRequest(
    [property: Required]
    [property: StringLength(200)]
    string Author,

    [property: Required]
    [property: StringLength(1000)]
    string Text);