using QuotesApi.Services;

namespace QuotesApi.Tests;

public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}