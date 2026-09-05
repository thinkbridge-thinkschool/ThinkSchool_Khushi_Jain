namespace QuotesApi.Models;

/// <summary>Settings for the relay that drains the outbox.</summary>
public sealed class OutboxOptions
{
    /// <summary>False leaves the rows written but starts no relay, which is what the test host wants.</summary>
    public bool Enabled { get; init; } = true;

    public int BatchSize { get; init; } = 50;

    /// <summary>The backstop, not the usual path: a write wakes the relay immediately, and this catches whatever no signal reached it for.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the relay may keep publishing after a stop is requested, before it leaves the rest for the next process.</summary>
    public TimeSpan DrainBudget { get; init; } = TimeSpan.FromSeconds(2);
}
