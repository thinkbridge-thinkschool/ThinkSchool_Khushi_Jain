using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Contracts;

public sealed record RegisterRequest(
    [property: Required]
    [property: EmailAddress]
    string Email,

    [property: Required]
    [property: MinLength(8)]
    string Password);
