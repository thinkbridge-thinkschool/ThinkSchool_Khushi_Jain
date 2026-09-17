using DocBook.Scheduling.Application;
using DocBook.Scheduling.Domain;

namespace DocBook.Application.Tests;

public class SweepRemindersHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 3, 2);

    // A sweep that loads only the far date never reminds an appointment booked for this afternoon.
    [Fact]
    public async Task The_sweep_covers_today_through_the_lead_time()
    {
        var schedules = new FakeScheduleRepository();

        await Sweep(schedules, TimeSpan.FromHours(24));

        Assert.Equal(Today, schedules.PagedFrom);
        Assert.Equal(Today.AddDays(1), schedules.PagedTo);
    }

    [Fact]
    public async Task An_appointment_later_today_is_reminded()
    {
        var schedule = DayWithAppointmentAt(11);
        var schedules = new FakeScheduleRepository(schedule);

        var swept = await Sweep(schedules, TimeSpan.FromHours(24));

        Assert.Equal(1, swept);
        Assert.IsType<AppointmentReminderDue>(Assert.Single(schedule.DomainEvents));
    }

    [Fact]
    public async Task An_appointment_past_the_lead_time_is_left_alone()
    {
        var schedule = DayWithAppointmentAt(11);
        var schedules = new FakeScheduleRepository(schedule);

        await Sweep(schedules, TimeSpan.FromHours(1));

        Assert.Empty(schedule.DomainEvents);
    }

    [Fact]
    public async Task The_sweep_works_through_every_page()
    {
        var schedules = new FakeScheduleRepository(
            DayWithAppointmentAt(11),
            DayWithAppointmentAt(12),
            DayWithAppointmentAt(13));

        var swept = await Sweep(schedules, TimeSpan.FromHours(24), pageSize: 2);

        Assert.Equal(3, swept);
        Assert.Equal(2, schedules.SaveCount);
    }

    // A page is a unit of work, so an interrupted sweep keeps the reminders it has already stamped.
    [Fact]
    public async Task A_sweep_with_nothing_to_do_saves_nothing()
    {
        var schedules = new FakeScheduleRepository();

        var swept = await Sweep(schedules, TimeSpan.FromHours(24));

        Assert.Equal(0, swept);
        Assert.Equal(0, schedules.SaveCount);
    }

    private static Task<int> Sweep(FakeScheduleRepository schedules, TimeSpan leadTime, int pageSize = 50) =>
        new SweepRemindersHandler(schedules, new FixedClock(Now))
            .HandleAsync(leadTime, pageSize, CancellationToken.None);

    private static DoctorDaySchedule DayWithAppointmentAt(int hour)
    {
        var schedule = DoctorDaySchedule.Open(
            new DoctorId(Guid.NewGuid()),
            Today,
            new TimeOnly(9, 0),
            new TimeOnly(17, 0));

        var start = new DateTimeOffset(2026, 3, 2, hour, 0, 0, TimeSpan.Zero);
        schedule.Book(new PatientId(Guid.NewGuid()), new TimeSlot(start, start.AddMinutes(30)), "check-up", Now);
        schedule.ClearDomainEvents();

        return schedule;
    }
}
