using DocBook.Scheduling.Domain;

namespace DocBook.Scheduling.Application;

public sealed record CancelAppointment(Guid AppointmentId, string Reason);

public sealed class CancelAppointmentHandler(IDoctorDayScheduleRepository schedules, TimeProvider clock)
{
    public async Task<bool> HandleAsync(CancelAppointment command, CancellationToken cancellationToken)
    {
        var appointmentId = new AppointmentId(command.AppointmentId);
        var schedule = await schedules.FindByAppointmentAsync(appointmentId, cancellationToken);

        if (schedule is null)
        {
            return false;
        }

        schedule.Cancel(appointmentId, command.Reason, clock.GetUtcNow());
        await schedules.SaveChangesAsync(cancellationToken);
        return true;
    }
}
