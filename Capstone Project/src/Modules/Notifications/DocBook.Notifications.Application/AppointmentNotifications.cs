using DocBook.Patients.Contracts;
using DocBook.Scheduling.Contracts;
using DocBook.SharedKernel;

namespace DocBook.Notifications.Application;

// Notifications knows Scheduling only through its published events and Patients only through the directory.
public sealed class AppointmentBookedNotification(IPatientDirectory patients, INotificationSender sender)
    : IIntegrationEventHandler<AppointmentBookedIntegrationEvent>
{
    public async Task HandleAsync(AppointmentBookedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is confirmed",
            $"We have you booked for {integrationEvent.Start:f} UTC.",
            cancellationToken);
    }
}

public sealed class AppointmentCancelledNotification(IPatientDirectory patients, INotificationSender sender)
    : IIntegrationEventHandler<AppointmentCancelledIntegrationEvent>
{
    public async Task HandleAsync(AppointmentCancelledIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is cancelled",
            $"Your appointment on {integrationEvent.Start:f} UTC is cancelled: {integrationEvent.Reason}",
            cancellationToken);
    }
}

public sealed class AppointmentReminderNotification(IPatientDirectory patients, INotificationSender sender)
    : IIntegrationEventHandler<AppointmentReminderDueIntegrationEvent>
{
    public async Task HandleAsync(AppointmentReminderDueIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await patients.FindAsync(integrationEvent.PatientId, cancellationToken) is not { } patient)
        {
            return;
        }

        await sender.SendAsync(
            patient,
            "Your appointment is tomorrow",
            $"A reminder that you are booked for {integrationEvent.Start:f} UTC.",
            cancellationToken);
    }
}
