using DocBook.Scheduling.Contracts;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Scheduling.Application;

// Where the module's private domain events become the public facts other modules subscribe to.
public sealed class AppointmentBookedPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<AppointmentBooked>
{
    public Task HandleAsync(AppointmentBooked domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new AppointmentBookedIntegrationEvent(
            domainEvent.AppointmentId.Value,
            domainEvent.DoctorId.Value,
            domainEvent.PatientId.Value,
            domainEvent.Slot.Start,
            domainEvent.Slot.End));

        return Task.CompletedTask;
    }
}

public sealed class AppointmentCancelledPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<AppointmentCancelled>
{
    public Task HandleAsync(AppointmentCancelled domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new AppointmentCancelledIntegrationEvent(
            domainEvent.AppointmentId.Value,
            domainEvent.DoctorId.Value,
            domainEvent.PatientId.Value,
            domainEvent.Slot.Start,
            domainEvent.Reason));

        return Task.CompletedTask;
    }
}

public sealed class AppointmentReminderDuePublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<AppointmentReminderDue>
{
    public Task HandleAsync(AppointmentReminderDue domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new AppointmentReminderDueIntegrationEvent(
            domainEvent.AppointmentId.Value,
            domainEvent.DoctorId.Value,
            domainEvent.PatientId.Value,
            domainEvent.Slot.Start));

        return Task.CompletedTask;
    }
}
