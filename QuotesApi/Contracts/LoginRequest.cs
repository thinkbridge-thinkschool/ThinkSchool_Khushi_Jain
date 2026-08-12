using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Contracts;

public sealed record LoginRequest(
    [property: Required]
    string Email,

    [property: Required]
    string Password);
