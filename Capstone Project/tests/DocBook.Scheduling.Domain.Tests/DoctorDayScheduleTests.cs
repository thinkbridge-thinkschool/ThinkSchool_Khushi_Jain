using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain.Tests;

public class DoctorDayScheduleTests
{
    private static readonly DateOnly Date = new(2026, 3, 2);
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_rejects_a_day_that_closes_before_it_opens()
    {
        var error = Assert.Throws<DomainException>(() =>
            DoctorDaySchedule.Open(new DoctorId(Guid.NewGuid()), Date, new TimeOnly(17, 0), new TimeOnly(9, 0)));

        Assert.Equal("day_closes_before_it_opens", error.Code);
    }

    [Fact]
    public void Book_adds_the_appointment_and_raises_the_event()
    {
        var schedule = OpenDay();

        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.Single(schedule.Appointments);
        Assert.IsType<AppointmentBooked>(Assert.Single(schedule.DomainEvents));
    }

    [Fact]
    public void Book_rejects_a_slot_outside_the_opening_hours()
    {
        var schedule = OpenDay();

        var error = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), SlotAt(8, 30, 30), "early bird", Now));

        Assert.Equal("slot_unavailable", error.Code);
    }

    [Fact]
    public void Book_rejects_a_slot_that_overlaps_an_active_appointment()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        var error = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), SlotAt(10, 15, 30), "second opinion", Now));

        Assert.Equal("slot_unavailable", error.Code);
    }

    // The refusal must not say which of the two happened, or it answers whether the doctor is busy.
    [Fact]
    public void Book_gives_a_closed_hour_and_a_taken_slot_the_same_code()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        var closed = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), SlotAt(8, 30, 30), "early bird", Now));

        var taken = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), Slot(10), "second opinion", Now));

        Assert.Equal(closed.Code, taken.Code);
    }

    [Fact]
    public void Book_rejects_a_slot_that_has_already_started()
    {
        var schedule = OpenDay();

        var error = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), Slot(10), "annual check-up", Now.AddHours(3)));

        Assert.Equal("slot_in_past", error.Code);
    }

    [Fact]
    public void Book_rejects_a_slot_on_another_day()
    {
        var schedule = OpenDay();
        var start = new DateTimeOffset(2026, 3, 3, 10, 0, 0, TimeSpan.Zero);

        var error = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), new TimeSlot(start, start.AddMinutes(30)), "annual check-up", Now));

        Assert.Equal("slot_wrong_day", error.Code);
    }

    [Fact]
    public void Book_rejects_a_missing_reason()
    {
        var schedule = OpenDay();

        var error = Assert.Throws<DomainException>(() =>
            schedule.Book(NewPatient(), Slot(10), "   ", Now));

        Assert.Equal("reason_required", error.Code);
    }

    [Fact]
    public void Book_rejects_a_reason_past_the_length_limit()
    {
        var schedule = OpenDay();
        var reason = new string('x', Appointment.MaxReasonLength + 1);

        var error = Assert.Throws<DomainException>(() => schedule.Book(NewPatient(), Slot(10), reason, Now));

        Assert.Equal("reason_too_long", error.Code);
    }

    [Fact]
    public void Book_reuses_a_slot_that_a_cancellation_freed()
    {
        var schedule = OpenDay();
        var first = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.Cancel(first.Id, "patient is away", Now);

        var second = schedule.Book(NewPatient(), Slot(10), "second opinion", Now);

        Assert.Equal(AppointmentStatus.Cancelled, first.Status);
        Assert.Equal(AppointmentStatus.Booked, second.Status);
    }

    [Fact]
    public void Cancel_raises_the_event_with_the_reason_it_was_given()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.ClearDomainEvents();

        schedule.Cancel(appointment.Id, "patient is away", Now);

        var cancelled = Assert.IsType<AppointmentCancelled>(Assert.Single(schedule.DomainEvents));
        Assert.Equal("patient is away", cancelled.Reason);
    }

    [Fact]
    public void Cancel_rejects_an_unknown_appointment()
    {
        var schedule = OpenDay();

        var error = Assert.Throws<DomainException>(() =>
            schedule.Cancel(AppointmentId.New(), "changed my mind", Now));

        Assert.Equal("appointment_unknown", error.Code);
    }

    [Fact]
    public void Cancel_rejects_an_appointment_that_is_already_cancelled()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.Cancel(appointment.Id, "patient is away", Now);

        var error = Assert.Throws<DomainException>(() =>
            schedule.Cancel(appointment.Id, "patient is away", Now));

        Assert.Equal("appointment_not_active", error.Code);
    }

    [Fact]
    public void Cancel_rejects_an_appointment_that_has_already_started()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        var error = Assert.Throws<DomainException>(() =>
            schedule.Cancel(appointment.Id, "running late", Now.AddHours(3)));

        Assert.Equal("appointment_already_started", error.Code);
    }

    [Fact]
    public void FreeSlots_fills_the_opening_hours_when_nothing_is_booked()
    {
        var schedule = OpenDay();

        var free = schedule.FreeSlots(TimeSpan.FromMinutes(30), Now);

        Assert.Equal(16, free.Count);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(free[0].Start.UtcDateTime));
        Assert.Equal(new TimeOnly(17, 0), TimeOnly.FromDateTime(free[^1].End.UtcDateTime));
    }

    [Fact]
    public void FreeSlots_leaves_out_a_slot_an_appointment_holds()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        var free = schedule.FreeSlots(TimeSpan.FromMinutes(30), Now);

        Assert.DoesNotContain(free, slot => slot.Start == Slot(10).Start);
        Assert.Equal(15, free.Count);
    }

    [Fact]
    public void FreeSlots_offers_a_slot_a_cancellation_freed()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.Cancel(appointment.Id, "patient is away", Now);

        var free = schedule.FreeSlots(TimeSpan.FromMinutes(30), Now);

        Assert.Contains(free, slot => slot.Start == Slot(10).Start);
    }

    [Fact]
    public void FreeSlots_leaves_out_a_slot_that_has_already_started()
    {
        var schedule = OpenDay();

        var free = schedule.FreeSlots(TimeSpan.FromMinutes(30), Now.AddHours(3));

        Assert.All(free, slot => Assert.True(slot.Start > Now.AddHours(3)));
        Assert.Equal(11, free.Count);
    }

    [Fact]
    public void FreeSlots_stops_before_a_slot_that_would_run_past_closing()
    {
        var schedule = OpenDay();
        var closes = new DateTimeOffset(Date.ToDateTime(new TimeOnly(17, 0)), TimeSpan.Zero);

        var free = schedule.FreeSlots(TimeSpan.FromMinutes(45), Now);

        Assert.Equal(10, free.Count);
        Assert.All(free, slot => Assert.True(slot.End <= closes));
    }

    [Fact]
    public void FreeSlots_rejects_a_length_of_nothing()
    {
        var schedule = OpenDay();

        var error = Assert.Throws<DomainException>(() => schedule.FreeSlots(TimeSpan.Zero, Now));

        Assert.Equal("slot_length_invalid", error.Code);
    }

    [Fact]
    public void MarkRemindersDue_raises_each_appointment_once()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.ClearDomainEvents();

        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(24));
        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(24));

        Assert.IsType<AppointmentReminderDue>(Assert.Single(schedule.DomainEvents));
    }

    [Fact]
    public void MarkRemindersDue_reminds_an_appointment_later_the_same_day()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.ClearDomainEvents();

        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(24));

        Assert.Single(schedule.DomainEvents);
    }

    [Fact]
    public void MarkRemindersDue_leaves_an_appointment_past_the_lead_time()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.ClearDomainEvents();

        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(1));

        Assert.Empty(schedule.DomainEvents);
    }

    [Fact]
    public void MarkRemindersDue_leaves_a_cancelled_appointment()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);
        schedule.Cancel(appointment.Id, "patient is away", Now);
        schedule.ClearDomainEvents();

        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(24));

        Assert.Empty(schedule.DomainEvents);
    }

    private static DoctorDaySchedule OpenDay() =>
        DoctorDaySchedule.Open(new DoctorId(Guid.NewGuid()), Date, new TimeOnly(9, 0), new TimeOnly(17, 0));

    private static PatientId NewPatient() => new(Guid.NewGuid());

    private static TimeSlot Slot(int hour, int minutes = 30) => SlotAt(hour, 0, minutes);

    private static TimeSlot SlotAt(int hour, int minute, int minutes)
    {
        var start = new DateTimeOffset(2026, 3, 2, hour, minute, 0, TimeSpan.Zero);
        return new TimeSlot(start, start.AddMinutes(minutes));
    }
}
