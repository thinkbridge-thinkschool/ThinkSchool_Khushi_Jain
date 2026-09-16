using DocBook.Notifications.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Notifications.Infrastructure;

public sealed class HandledMessageLog(NotificationsDbContext context, TimeProvider clock) : IHandledMessageLog
{
    public Task<bool> WasHandledAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken) =>
        context.HandledMessages.AnyAsync(
            message => message.AppointmentId == appointmentId && message.Kind == kind,
            cancellationToken);

    public async Task RecordAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken)
    {
        context.HandledMessages.Add(new HandledMessage
        {
            AppointmentId = appointmentId,
            Kind = kind,
            HandledAt = clock.GetUtcNow()
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Another instance sent this between the check and now, so the row already says so.
            context.ChangeTracker.Clear();
        }
    }

    // 2627 is a primary key violation and 2601 a unique index one; this table's key can raise either.
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2627 or 2601 };
}
