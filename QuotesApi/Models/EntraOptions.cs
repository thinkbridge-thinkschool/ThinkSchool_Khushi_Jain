namespace QuotesApi.Models;

public sealed class EntraOptions
{
    public string TenantId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;
}
