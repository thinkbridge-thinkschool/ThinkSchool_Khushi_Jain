using DocBook.Scheduling.Application;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Application.Tests;

public class CancelAppointmentHandlerTests
{
    private static readonly Guid PatientGuid = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private static readonly Actor Patient = new(PatientGuid, ActorRole.Patient);
    private static readonly Actor Staff = new(Guid.NewGuid(), ActorRole.Staff);

    private readonly RecordingAuditTrail _audit = new();
    private readonly DoctorDaySchedule _schedule;
    private readonly FakeScheduleRepository _schedules;
    private readonly Guid _appointmentId;

    public CancelAppointmentHandlerTests()
    {
        _schedule = DoctorDaySchedule.Open(
            new DoctorId(Guid.NewGuid()),
            new DateOnly(2026, 3, 2),
            new TimeOnly(9, 0),
            new TimeOnly(17, 0));

        var start = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
        var appointment = _schedule.Book(
            new PatientId(PatientGuid),
            new TimeSlot(start, start.AddMinutes(30)),
            "annual check-up",
            Now);

        _appointmentId = appointment.Id.Value;
        _schedules = new FakeScheduleRepository(_schedule);
    }

    [Fact]
    public async Task A_patient_cancels_their_own_appointment()
    {
        var outcome = await Handle(Patient, _appointmentId);

        Assert.Equal(CancellationOutcome.Cancelled, outcome);
        Assert.Equal(AppointmentStatus.Cancelled, _schedule.Appointments[0].Status);
        Assert.Equal(1, _schedules.SaveCount);
    }

    [Fact]
    public async Task Staff_cancel_for_anyone()
    {
        var outcome = await Handle(Staff, _appointmentId);

        Assert.Equal(CancellationOutcome.Cancelled, outcome);
    }

    [Fact]
    public async Task An_appointment_that_does_not_exist_is_not_found()
    {
        var outcome = await Handle(Patient, Guid.NewGuid());

        Assert.Equal(CancellationOutcome.NotFound, outcome);
        Assert.Equal(0, _schedules.SaveCount);
    }

    // The aggregate's rules are about the day. Whose appointment it is only the use case knows.
    [Fact]
    public async Task A_patient_may_not_cancel_someone_elses_appointment()
    {
        var outcome = await Handle(new Actor(Guid.NewGuid(), ActorRole.Patient), _appointmentId);

        Assert.Equal(CancellationOutcome.Forbidden, outcome);
        Assert.Equal(AppointmentStatus.Booked, _schedule.Appointments[0].Status);
    }

    [Fact]
    public async Task A_refused_cancellation_is_written_to_the_audit_trail()
    {
        var stranger = new Actor(Guid.NewGuid(), ActorRole.Patient);

        await Handle(stranger, _appointmentId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("appointment.cancel_refused", entry.Action);
        Assert.Equal(stranger, entry.Actor);
        Assert.Equal(1, _schedules.SaveCount);
    }

    [Fact]
    public async Task Cancelling_is_written_to_the_audit_trail()
    {
        await Handle(Patient, _appointmentId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("appointment.cancelled", entry.Action);
        Assert.Equal(_appointmentId, entry.SubjectId);
    }

    [Fact]
    public async Task An_appointment_that_has_started_is_refused_by_the_aggregate()
    {
        var handler = new CancelAppointmentHandler(_schedules, _audit, new FixedClock(Now.AddHours(3)));

        var error = await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(
            new CancelAppointment(_appointmentId, "running late"),
            Patient,
            CancellationToken.None));

        Assert.Equal("appointment_already_started", error.Code);
    }

    private Task<CancellationOutcome> Handle(Actor actor, Guid appointmentId) =>
        new CancelAppointmentHandler(_schedules, _audit, new FixedClock(Now)).HandleAsync(
            new CancelAppointment(appointmentId, "patient is away"),
            actor,
            CancellationToken.None);
}
