using DocBook.Patients.Contracts;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Scheduling.Application;

public sealed record BookAppointment(
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason);

public enum BookingOutcome
{
    Booked = 1,
    Forbidden = 2,
    Unavailable = 3,
}

public sealed record BookingResult(BookingOutcome Outcome, Guid AppointmentId);

public sealed class BookAppointmentHandler(
    IDoctorDayScheduleRepository schedules,
    IPatientDirectory patients,
    IAuditTrail audit,
    TimeProvider clock)
{
    public async Task<BookingResult> HandleAsync(
        BookAppointment command,
        Actor actor,
        CancellationToken cancellationToken)
    {
        // A patient books only for themselves; staff book for anyone.
        if (!actor.IsStaff && !actor.Is(command.PatientId))
        {
            return new BookingResult(BookingOutcome.Forbidden, Guid.Empty);
        }

        var date = DateOnly.FromDateTime(command.Start.UtcDateTime);
        var schedule = await schedules.FindAsync(new DoctorId(command.DoctorId), date, cancellationToken);

        // One outcome for an unknown patient and an unopened day, so neither probes the other.
        if (schedule is null || await patients.FindAsync(command.PatientId, cancellationToken) is null)
        {
            return new BookingResult(BookingOutcome.Unavailable, Guid.Empty);
        }

        var appointment = schedule.Book(
            new PatientId(command.PatientId),
            new TimeSlot(command.Start, command.End),
            command.Reason,
            clock.GetUtcNow());

        audit.Record(actor, "appointment.booked", appointment.Id.Value);

        await schedules.SaveChangesAsync(cancellationToken);

        return new BookingResult(BookingOutcome.Booked, appointment.Id.Value);
    }
}
