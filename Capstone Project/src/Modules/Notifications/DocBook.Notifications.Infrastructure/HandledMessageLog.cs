using DocBook.Notifications.Application;
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
        catch (DbUpdateException)
        {
            // The composite key is the only constraint here, so this is another instance having sent
            // it between the check and now. The message is delivered either way and the row stands.
            context.ChangeTracker.Clear();
        }
    }
}
