using DocBook.Patients.Contracts;
using DocBook.Scheduling.Domain;

namespace DocBook.Scheduling.Application;

public sealed record BookAppointment(
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason);

public sealed class BookAppointmentHandler(
    IDoctorDayScheduleRepository schedules,
    IPatientDirectory patients,
    TimeProvider clock)
{
    // Returns null when the patient or the doctor's day does not exist. Rule breaks throw DomainException.
    public async Task<Guid?> HandleAsync(BookAppointment command, CancellationToken cancellationToken)
    {
        if (await patients.FindAsync(command.PatientId, cancellationToken) is null)
        {
            return null;
        }

        var date = DateOnly.FromDateTime(command.Start.UtcDateTime);
        var schedule = await schedules.FindAsync(new DoctorId(command.DoctorId), date, cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        var appointment = schedule.Book(
            new PatientId(command.PatientId),
            new TimeSlot(command.Start, command.End),
            command.Reason,
            clock.GetUtcNow());

        await schedules.SaveChangesAsync(cancellationToken);
        return appointment.Id.Value;
    }
}
