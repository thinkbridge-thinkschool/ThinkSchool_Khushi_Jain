using DocBook.Notifications.Application;

namespace DocBook.Notifications.Infrastructure;

// The first data Notifications owns, and it is here for an operational reason rather than a domain one.
public sealed class HandledMessage
{
    public required Guid AppointmentId { get; init; }

    public required NotificationKind Kind { get; init; }

    public DateTimeOffset HandledAt { get; init; }
}
