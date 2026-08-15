namespace QuotesApi.Models;

/// <summary>
/// Credentials for the development-only starter account. Both values come from
/// user secrets; when either is absent the seed is skipped entirely, so a clone
/// with no secrets configured still starts.
/// </summary>
public sealed class SeedOptions
{
    public string AdminEmail { get; init; } = string.Empty;

    public string AdminPassword { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AdminEmail) &&
        !string.IsNullOrWhiteSpace(AdminPassword);
}
