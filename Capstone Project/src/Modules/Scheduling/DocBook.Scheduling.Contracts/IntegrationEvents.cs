using DocBook.SharedKernel;

namespace DocBook.Scheduling.Contracts;

// The whole public surface of Scheduling. Ids are plain Guids so no module needs Scheduling's types.
public sealed record AppointmentBookedIntegrationEvent(
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset Start,
    DateTimeOffset End) : IIntegrationEvent;

public sealed record AppointmentCancelledIntegrationEvent(
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset Start,
    string Reason) : IIntegrationEvent;

public sealed record AppointmentReminderDueIntegrationEvent(
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset Start) : IIntegrationEvent;
