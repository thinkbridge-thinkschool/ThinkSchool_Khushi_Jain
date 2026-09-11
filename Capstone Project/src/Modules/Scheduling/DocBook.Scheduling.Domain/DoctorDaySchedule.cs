using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain;

// One doctor, one date: both appointments sit in the same boundary, so double booking cannot happen.
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
            throw new DomainException("day_closes_before_it_opens", "A day must close after it opens.");
        }

        return new DoctorDaySchedule(DoctorDayScheduleId.New(), doctorId, date, opensAt, closesAt);
    }

    public Appointment Book(PatientId patientId, TimeSlot slot, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("reason_required", "An appointment needs a reason.");
        }

        if (reason.Length > Appointment.MaxReasonLength)
        {
            throw new DomainException("reason_too_long", "That reason is too long.");
        }

        if (slot.Start <= now)
        {
            throw new DomainException("slot_in_past", "An appointment cannot start in the past.");
        }

        if (DateOnly.FromDateTime(slot.Start.UtcDateTime) != Date || DateOnly.FromDateTime(slot.End.UtcDateTime) != Date)
        {
            throw new DomainException("slot_wrong_day", "An appointment must fall on the day it is booked against.");
        }

        // Closed and already taken share one code, so a refusal cannot be read as "someone is in it".
        if (TimeOnly.FromDateTime(slot.Start.UtcDateTime) < OpensAt || TimeOnly.FromDateTime(slot.End.UtcDateTime) > ClosesAt)
        {
            throw new DomainException("slot_unavailable", "An appointment must fall inside the doctor's opening hours.");
        }

        if (_appointments.Any(appointment => appointment.IsActive && appointment.Slot.Overlaps(slot)))
        {
            throw new DomainException("slot_unavailable", "The doctor already has an appointment in that slot.");
        }

        var booked = new Appointment(AppointmentId.New(), patientId, slot, reason);
        _appointments.Add(booked);
        Raise(new AppointmentBooked(booked.Id, DoctorId, patientId, slot, now));
        return booked;
    }

    public void Cancel(AppointmentId appointmentId, string reason, DateTimeOffset now)
    {
        var appointment = Find(appointmentId);

        if (reason.Length > Appointment.MaxReasonLength)
        {
            throw new DomainException("reason_too_long", "That reason is too long.");
        }

        if (!appointment.IsActive)
        {
            throw new DomainException("appointment_not_active", "Only an active appointment can be cancelled.");
        }

        if (appointment.Slot.Start <= now)
        {
            throw new DomainException("appointment_already_started", "An appointment can only be cancelled before it starts.");
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

    // What is still free, never who holds the rest of the day or how many of them there are.
    public IReadOnlyList<TimeSlot> FreeSlots(TimeSpan length, DateTimeOffset now)
    {
        if (length <= TimeSpan.Zero)
        {
            throw new DomainException("slot_length_invalid", "A slot must be longer than nothing.");
        }

        var free = new List<TimeSlot>();
        var start = new DateTimeOffset(Date.ToDateTime(OpensAt), TimeSpan.Zero);
        var closes = new DateTimeOffset(Date.ToDateTime(ClosesAt), TimeSpan.Zero);

        while (start + length <= closes)
        {
            var candidate = new TimeSlot(start, start + length);

            if (candidate.Start > now &&
                !_appointments.Any(appointment => appointment.IsActive && appointment.Slot.Overlaps(candidate)))
            {
                free.Add(candidate);
            }

            start += length;
        }

        return free;
    }

    private Appointment Find(AppointmentId appointmentId) =>
        _appointments.SingleOrDefault(appointment => appointment.Id == appointmentId)
        ?? throw new DomainException("appointment_unknown", "Unknown appointment.");
}
