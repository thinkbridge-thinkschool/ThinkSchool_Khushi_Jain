using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain;

public sealed record AppointmentBooked(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    TimeSlot Slot,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record AppointmentCancelled(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    TimeSlot Slot,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record AppointmentReminderDue(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    TimeSlot Slot,
    DateTimeOffset OccurredAt) : IDomainEvent;
