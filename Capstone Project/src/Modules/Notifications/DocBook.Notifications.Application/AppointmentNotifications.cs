using DocBook.Patients.Contracts;
using DocBook.Scheduling.Contracts;
using DocBook.SharedKernel;

namespace DocBook.Notifications.Application;

// Notifications knows Scheduling only through its published events and Patients only through the directory.
public sealed class AppointmentBookedNotification(
    IPatientDirectory patients,
    INotificationSender sender,
    IHandledMessageLog handled)
    : IIntegrationEventHandler<AppointmentBookedIntegrationEvent>
{
    public async Task HandleAsync(AppointmentBookedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        // Delivery is at least once, so a redelivered booking must not confirm itself twice.
        if (await handled.WasHandledAsync(integrationEvent.AppointmentId, NotificationKind.Confirmation, cancellationToken))
        {
            return;
        }

        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is confirmed",
            $"We have you booked for {integrationEvent.Start:f} UTC.",
            cancellationToken);

        await handled.RecordAsync(integrationEvent.AppointmentId, NotificationKind.Confirmation, cancellationToken);
    }
}

public sealed class AppointmentCancelledNotification(
    IPatientDirectory patients,
    INotificationSender sender,
    IHandledMessageLog handled)
    : IIntegrationEventHandler<AppointmentCancelledIntegrationEvent>
{
    public async Task HandleAsync(AppointmentCancelledIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await handled.WasHandledAsync(integrationEvent.AppointmentId, NotificationKind.Cancellation, cancellationToken))
        {
            return;
        }

        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is cancelled",
            $"Your appointment on {integrationEvent.Start:f} UTC is cancelled: {integrationEvent.Reason}",
            cancellationToken);

        await handled.RecordAsync(integrationEvent.AppointmentId, NotificationKind.Cancellation, cancellationToken);
    }
}

public sealed class AppointmentReminderNotification(
    IPatientDirectory patients,
    INotificationSender sender,
    IHandledMessageLog handled)
    : IIntegrationEventHandler<AppointmentReminderDueIntegrationEvent>
{
    public async Task HandleAsync(AppointmentReminderDueIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await handled.WasHandledAsync(integrationEvent.AppointmentId, NotificationKind.Reminder, cancellationToken))
        {
            return;
        }

        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is tomorrow",
            $"A reminder that you are booked for {integrationEvent.Start:f} UTC.",
            cancellationToken);

        await handled.RecordAsync(integrationEvent.AppointmentId, NotificationKind.Reminder, cancellationToken);
    }
}
