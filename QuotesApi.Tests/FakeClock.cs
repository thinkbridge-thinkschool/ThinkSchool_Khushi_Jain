using QuotesApi.Time;

namespace QuotesApi.Tests;

/// <summary>
/// Mutable IClock substitute for integration tests that need to simulate
/// time passing (e.g. refresh-token expiry) without waiting in real time.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
