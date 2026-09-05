namespace QuotesApi.Models;

/// <summary>
/// The cache in front of the hot read. Redis is the second tier and is
/// optional: without a connection string HybridCache runs on its in-memory tier
/// alone, which is a smaller cache rather than no cache.
/// </summary>
public sealed class CacheOptions
{
    public string RedisConnectionString { get; init; } = string.Empty;

    /// <summary>How long an entry lives overall, in memory and in Redis alike.</summary>
    public TimeSpan Expiration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the in-memory tier keeps its own copy. Never longer than Expiration.</summary>
    public TimeSpan LocalExpiration { get; init; } = TimeSpan.FromMinutes(1);

    public bool HasRedis => !string.IsNullOrWhiteSpace(RedisConnectionString);
}
