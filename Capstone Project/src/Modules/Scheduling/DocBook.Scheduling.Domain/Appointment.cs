using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain;

public enum AppointmentStatus
{
    Booked = 1,
    Cancelled = 2,
}

// An entity inside the DoctorDaySchedule aggregate. It only ever changes through its root.
public sealed class Appointment : Entity<AppointmentId>
{
    internal Appointment(AppointmentId id, PatientId patientId, TimeSlot slot, string reason) : base(id)
    {
        PatientId = patientId;
        Slot = slot;
        Reason = reason;
        Status = AppointmentStatus.Booked;
    }

    private Appointment()
    {
    }

    public PatientId PatientId { get; private set; }

    public TimeSlot Slot { get; private set; }

    public AppointmentStatus Status { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset? ReminderSentAt { get; private set; }

    public bool IsActive => Status == AppointmentStatus.Booked;

    internal void Cancel() => Status = AppointmentStatus.Cancelled;

    internal void MarkReminderSent(DateTimeOffset at) => ReminderSentAt = at;
}
