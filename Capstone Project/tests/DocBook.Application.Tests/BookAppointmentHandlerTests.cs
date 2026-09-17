using DocBook.Patients.Contracts;
using DocBook.Scheduling.Application;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Application.Tests;

public class BookAppointmentHandlerTests
{
    private static readonly Guid Doctor = Guid.NewGuid();
    private static readonly Guid PatientGuid = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 2);
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly Actor Patient = new(PatientGuid, ActorRole.Patient);
    private static readonly Actor Staff = new(Guid.NewGuid(), ActorRole.Staff);

    private readonly FakeScheduleRepository _schedules = new(OpenDay());
    private readonly RecordingAuditTrail _audit = new();

    private readonly FakePatientDirectory _patients =
        new(new PatientContact(PatientGuid, "Ada Lovelace", "ada@example.com"));

    [Fact]
    public async Task A_patient_books_for_themselves()
    {
        var result = await Handle(Patient, PatientGuid);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);
        Assert.NotEqual(Guid.Empty, result.AppointmentId);
        Assert.Single(_schedules.Schedules[0].Appointments);
        Assert.Equal(1, _schedules.SaveCount);
    }

    [Fact]
    public async Task Staff_book_for_a_patient()
    {
        var result = await Handle(Staff, PatientGuid);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);
    }

    // Refused before the lookup, so a refusal cannot be timed to learn whether the day exists.
    [Fact]
    public async Task A_patient_may_not_book_for_someone_else()
    {
        var result = await Handle(Patient, Guid.NewGuid());

        Assert.Equal(BookingOutcome.Forbidden, result.Outcome);
        Assert.Equal(0, _schedules.FindCalls);
        Assert.Equal(0, _schedules.SaveCount);
    }

    [Fact]
    public async Task A_day_that_was_never_opened_is_unavailable()
    {
        var schedules = new FakeScheduleRepository();
        var handler = new BookAppointmentHandler(schedules, _patients, _audit, new FixedClock(Now));

        var result = await handler.HandleAsync(Command(PatientGuid), Patient, CancellationToken.None);

        Assert.Equal(BookingOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, schedules.SaveCount);
    }

    // Neither answer may distinguish the other, or booking becomes a way to ask who is registered.
    [Fact]
    public async Task An_unknown_patient_answers_the_same_way_as_an_unopened_day()
    {
        var unknown = Guid.NewGuid();
        var handler = new BookAppointmentHandler(
            new FakeScheduleRepository(OpenDay()),
            _patients,
            _audit,
            new FixedClock(Now));

        var unknownPatient = await handler.HandleAsync(
            Command(unknown),
            new Actor(unknown, ActorRole.Patient),
            CancellationToken.None);

        var unopenedDay = await new BookAppointmentHandler(
            new FakeScheduleRepository(),
            _patients,
            _audit,
            new FixedClock(Now)).HandleAsync(Command(PatientGuid), Patient, CancellationToken.None);

        Assert.Equal(unopenedDay.Outcome, unknownPatient.Outcome);
        Assert.Equal(unopenedDay.AppointmentId, unknownPatient.AppointmentId);
    }

    [Fact]
    public async Task Booking_is_recorded_against_the_patient_who_asked()
    {
        var result = await Handle(Patient, PatientGuid);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("appointment.booked", entry.Action);
        Assert.Equal(result.AppointmentId, entry.SubjectId);
    }

    [Fact]
    public async Task A_slot_the_doctor_already_has_is_refused_by_the_aggregate()
    {
        await Handle(Patient, PatientGuid);

        var error = await Assert.ThrowsAsync<DomainException>(() => Handle(Patient, PatientGuid));

        Assert.Equal("slot_unavailable", error.Code);
    }

    [Fact]
    public async Task Nothing_is_saved_when_the_aggregate_refuses()
    {
        var handler = new BookAppointmentHandler(_schedules, _patients, _audit, new FixedClock(Now));
        var beforeOpening = new DateTimeOffset(2026, 3, 2, 8, 30, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(
            new BookAppointment(Doctor, PatientGuid, beforeOpening, beforeOpening.AddMinutes(30), "annual check-up"),
            Patient,
            CancellationToken.None));

        Assert.Equal(0, _schedules.SaveCount);
    }

    private Task<BookingResult> Handle(Actor actor, Guid patientId) =>
        new BookAppointmentHandler(_schedules, _patients, _audit, new FixedClock(Now))
            .HandleAsync(Command(patientId), actor, CancellationToken.None);

    private static BookAppointment Command(Guid patientId) =>
        new(Doctor, patientId, Start, Start.AddMinutes(30), "annual check-up");

    private static DoctorDaySchedule OpenDay() =>
        DoctorDaySchedule.Open(new DoctorId(Doctor), Date, new TimeOnly(9, 0), new TimeOnly(17, 0));
}
