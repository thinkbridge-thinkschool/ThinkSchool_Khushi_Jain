namespace DocBook.Notifications.Application;

// Together with the appointment id, this is the key the design says Notifications dedupes on.
public enum NotificationKind
{
    Confirmation,
    Cancellation,
    Reminder
}

public interface IHandledMessageLog
{
    Task<bool> WasHandledAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken);

    Task RecordAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken);
}
