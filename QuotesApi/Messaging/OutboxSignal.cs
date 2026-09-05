namespace QuotesApi.Messaging;

/// <summary>
/// Wakes the relay as soon as a row is written, so a publish does not wait for
/// the next poll. The poll is still what makes the outbox correct -- a signal
/// can be lost, and rows written by another process raise none at all -- so
/// this only makes the common case fast, and is never the difference between a
/// row being delivered and not.
/// </summary>
public sealed class OutboxSignal
{
    // Capacity of one: a single queued wake-up is enough, because the relay
    // drains every unsent row it finds rather than one row per signal.
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void NotifyPending()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already queued, which is all this needs to achieve.
        }
    }

    /// <summary>Returns as soon as a row is written, or when the backstop elapses.</summary>
    public async Task WaitAsync(TimeSpan backstop, CancellationToken cancellationToken) =>
        await _pending.WaitAsync(backstop, cancellationToken);
}
