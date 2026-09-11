using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;

namespace DocBook.Scheduling.Application;

public sealed record OpenDoctorDay(Guid DoctorId, DateOnly Date, TimeOnly OpensAt, TimeOnly ClosesAt);

public enum OpenDayOutcome
{
    Opened = 1,
    Forbidden = 2,
    AlreadyOpen = 3,
}

public sealed record OpenDayResult(OpenDayOutcome Outcome, Guid ScheduleId);

public sealed class OpenDoctorDayHandler(IDoctorDayScheduleRepository schedules, IAuditTrail audit)
{
    public async Task<OpenDayResult> HandleAsync(
        OpenDoctorDay command,
        Actor actor,
        CancellationToken cancellationToken)
    {
        // Opening a doctor's day is clinic work. A patient holding a valid token is still not staff.
        if (!actor.IsStaff)
        {
            return new OpenDayResult(OpenDayOutcome.Forbidden, Guid.Empty);
        }

        var doctorId = new DoctorId(command.DoctorId);

        if (await schedules.FindAsync(doctorId, command.Date, cancellationToken) is not null)
        {
            return new OpenDayResult(OpenDayOutcome.AlreadyOpen, Guid.Empty);
        }

        var schedule = DoctorDaySchedule.Open(doctorId, command.Date, command.OpensAt, command.ClosesAt);

        schedules.Add(schedule);
        audit.Record(actor, "day.opened", schedule.Id.Value);

        await schedules.SaveChangesAsync(cancellationToken);

        return new OpenDayResult(OpenDayOutcome.Opened, schedule.Id.Value);
    }
}
