using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Scheduling.Application;

public sealed record CancelAppointment(Guid AppointmentId, string Reason);

public enum CancellationOutcome
{
    Cancelled = 1,
    NotFound = 2,
    Forbidden = 3,
}

public sealed class CancelAppointmentHandler(
    IDoctorDayScheduleRepository schedules,
    IAuditTrail audit,
    TimeProvider clock)
{
    public async Task<CancellationOutcome> HandleAsync(
        CancelAppointment command,
        Actor actor,
        CancellationToken cancellationToken)
    {
        var appointmentId = new AppointmentId(command.AppointmentId);
        var schedule = await schedules.FindByAppointmentAsync(appointmentId, cancellationToken);

        if (schedule is null)
        {
            return CancellationOutcome.NotFound;
        }

        var appointment = schedule.Appointments.Single(candidate => candidate.Id == appointmentId);

        // The check the aggregate cannot make: its rules are about the day, not about who is asking.
        if (!actor.IsStaff && !actor.Is(appointment.PatientId.Value))
        {
            audit.Record(actor, "appointment.cancel_refused", appointmentId.Value);
            await schedules.SaveChangesAsync(cancellationToken);

            return CancellationOutcome.Forbidden;
        }

        schedule.Cancel(appointmentId, command.Reason, clock.GetUtcNow());
        audit.Record(actor, "appointment.cancelled", appointmentId.Value);

        await schedules.SaveChangesAsync(cancellationToken);

        return CancellationOutcome.Cancelled;
    }
}
