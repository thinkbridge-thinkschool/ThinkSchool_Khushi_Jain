namespace QuotesApi.Models;

/// <summary>
/// The broker integration events are published to and consumed from. The
/// connection string is a secret and is absent from appsettings.json; without
/// it the API logs its events instead, and starts no consumer.
/// </summary>
public sealed class ServiceBusOptions
{
    public string ConnectionString { get; init; } = string.Empty;

    public string Topic { get; init; } = "quote-events";

    public string AuditSubscription { get; init; } = "audit";

    public string ModerationSubscription { get; init; } = "moderation";

    /// <summary>Processors on the audit subscription. More than one is what makes them competing consumers.</summary>
    public int AuditConsumers { get; init; } = 2;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
