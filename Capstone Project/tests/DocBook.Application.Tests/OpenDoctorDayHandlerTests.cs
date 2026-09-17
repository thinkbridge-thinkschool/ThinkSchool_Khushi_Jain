using DocBook.Scheduling.Application;
using DocBook.SharedKernel;

namespace DocBook.Application.Tests;

public class OpenDoctorDayHandlerTests
{
    private static readonly Guid Doctor = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 2);
    private static readonly Actor Staff = new(Guid.NewGuid(), ActorRole.Staff);
    private static readonly Actor Patient = new(Guid.NewGuid(), ActorRole.Patient);

    private readonly FakeScheduleRepository _schedules = new();
    private readonly RecordingAuditTrail _audit = new();

    [Fact]
    public async Task Staff_open_the_day_and_it_is_saved()
    {
        var result = await Handle(Staff);

        Assert.Equal(OpenDayOutcome.Opened, result.Outcome);
        Assert.NotEqual(Guid.Empty, result.ScheduleId);
        Assert.Single(_schedules.Schedules);
        Assert.Equal(1, _schedules.SaveCount);
    }

    // A valid token is not a role. Every patient holds one.
    [Fact]
    public async Task A_patient_may_not_open_a_day()
    {
        var result = await Handle(Patient);

        Assert.Equal(OpenDayOutcome.Forbidden, result.Outcome);
        Assert.Empty(_schedules.Schedules);
        Assert.Equal(0, _schedules.SaveCount);
    }

    [Fact]
    public async Task A_day_that_is_already_open_is_refused()
    {
        await Handle(Staff);

        var result = await Handle(Staff);

        Assert.Equal(OpenDayOutcome.AlreadyOpen, result.Outcome);
        Assert.Single(_schedules.Schedules);
    }

    [Fact]
    public async Task Opening_a_day_is_recorded_against_the_member_of_staff_who_did_it()
    {
        var result = await Handle(Staff);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("day.opened", entry.Action);
        Assert.Equal(Staff, entry.Actor);
        Assert.Equal(result.ScheduleId, entry.SubjectId);
    }

    [Fact]
    public async Task A_day_that_closes_before_it_opens_is_refused_by_the_aggregate()
    {
        var handler = new OpenDoctorDayHandler(_schedules, _audit);

        await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(
            new OpenDoctorDay(Doctor, Date, new TimeOnly(17, 0), new TimeOnly(9, 0)),
            Staff,
            CancellationToken.None));
    }

    private Task<OpenDayResult> Handle(Actor actor) =>
        new OpenDoctorDayHandler(_schedules, _audit).HandleAsync(
            new OpenDoctorDay(Doctor, Date, new TimeOnly(9, 0), new TimeOnly(17, 0)),
            actor,
            CancellationToken.None);
}
