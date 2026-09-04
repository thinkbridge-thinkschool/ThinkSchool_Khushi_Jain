using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain.Tests;

public class DoctorDayScheduleTests
{
    private static readonly DateOnly Date = new(2026, 3, 2);
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

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

        Assert.Throws<DomainException>(() => schedule.Book(NewPatient(), Slot(8), "early bird", Now));
    }

    [Fact]
    public void Book_rejects_a_slot_that_overlaps_an_active_appointment()
    {
        var schedule = OpenDay();
        schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        Assert.Throws<DomainException>(() => schedule.Book(NewPatient(), Slot(10, 15), "second opinion", Now));
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
    public void Cancel_rejects_an_appointment_that_has_already_started()
    {
        var schedule = OpenDay();
        var appointment = schedule.Book(NewPatient(), Slot(10), "annual check-up", Now);

        Assert.Throws<DomainException>(() =>
            schedule.Cancel(appointment.Id, "running late", Now.AddHours(3)));
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

    private static DoctorDaySchedule OpenDay() =>
        DoctorDaySchedule.Open(new DoctorId(Guid.NewGuid()), Date, new TimeOnly(9, 0), new TimeOnly(17, 0));

    private static PatientId NewPatient() => new(Guid.NewGuid());

    private static TimeSlot Slot(int hour, int minutes = 30)
    {
        var start = new DateTimeOffset(2026, 3, 2, hour, 0, 0, TimeSpan.Zero);
        return new TimeSlot(start, start.AddMinutes(minutes));
    }
}
