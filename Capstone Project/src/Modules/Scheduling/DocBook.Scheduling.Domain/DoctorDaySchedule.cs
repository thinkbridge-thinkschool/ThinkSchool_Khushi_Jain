using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain;

// The aggregate root: one doctor, one date. Double booking is impossible because both appointments
// live inside the same consistency boundary.
public sealed class DoctorDaySchedule : AggregateRoot<DoctorDayScheduleId>
{
    private readonly List<Appointment> _appointments = [];

    private DoctorDaySchedule(
        DoctorDayScheduleId id,
        DoctorId doctorId,
        DateOnly date,
        TimeOnly opensAt,
        TimeOnly closesAt) : base(id)
    {
        DoctorId = doctorId;
        Date = date;
        OpensAt = opensAt;
        ClosesAt = closesAt;
    }

    private DoctorDaySchedule()
    {
    }

    public DoctorId DoctorId { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly OpensAt { get; private set; }

    public TimeOnly ClosesAt { get; private set; }

    public IReadOnlyList<Appointment> Appointments => _appointments;

    public static DoctorDaySchedule Open(DoctorId doctorId, DateOnly date, TimeOnly opensAt, TimeOnly closesAt)
    {
        if (closesAt <= opensAt)
        {
            throw new DomainException("A day must close after it opens.");
        }

        return new DoctorDaySchedule(DoctorDayScheduleId.New(), doctorId, date, opensAt, closesAt);
    }

    public Appointment Book(PatientId patientId, TimeSlot slot, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("An appointment needs a reason.");
        }

        if (slot.Start <= now)
        {
            throw new DomainException("An appointment cannot start in the past.");
        }

        if (DateOnly.FromDateTime(slot.Start.UtcDateTime) != Date || DateOnly.FromDateTime(slot.End.UtcDateTime) != Date)
        {
            throw new DomainException("An appointment must fall on the day it is booked against.");
        }

        if (TimeOnly.FromDateTime(slot.Start.UtcDateTime) < OpensAt || TimeOnly.FromDateTime(slot.End.UtcDateTime) > ClosesAt)
        {
            throw new DomainException("An appointment must fall inside the doctor's opening hours.");
        }

        if (_appointments.Any(appointment => appointment.IsActive && appointment.Slot.Overlaps(slot)))
        {
            throw new DomainException("The doctor already has an appointment in that slot.");
        }

        var booked = new Appointment(AppointmentId.New(), patientId, slot, reason);
        _appointments.Add(booked);
        Raise(new AppointmentBooked(booked.Id, DoctorId, patientId, slot, now));
        return booked;
    }

    public void Cancel(AppointmentId appointmentId, string reason, DateTimeOffset now)
    {
        var appointment = Find(appointmentId);

        if (!appointment.IsActive)
        {
            throw new DomainException("Only an active appointment can be cancelled.");
        }

        if (appointment.Slot.Start <= now)
        {
            throw new DomainException("An appointment can only be cancelled before it starts.");
        }

        appointment.Cancel();
        Raise(new AppointmentCancelled(appointment.Id, DoctorId, appointment.PatientId, appointment.Slot, reason, now));
    }

    // Stamps each appointment as reminded so a second sweep does not raise the event again.
    public void MarkRemindersDue(DateTimeOffset now, TimeSpan leadTime)
    {
        var due = _appointments.Where(appointment =>
            appointment.IsActive &&
            appointment.ReminderSentAt is null &&
            appointment.Slot.Start > now &&
            appointment.Slot.Start <= now + leadTime);

        foreach (var appointment in due)
        {
            appointment.MarkReminderSent(now);
            Raise(new AppointmentReminderDue(appointment.Id, DoctorId, appointment.PatientId, appointment.Slot, now));
        }
    }

    private Appointment Find(AppointmentId appointmentId) =>
        _appointments.SingleOrDefault(appointment => appointment.Id == appointmentId)
        ?? throw new DomainException("Unknown appointment.");
}
