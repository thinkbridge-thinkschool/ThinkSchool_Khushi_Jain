using DocBook.Scheduling.Domain;

namespace DocBook.Scheduling.Application;

public sealed record OpenDoctorDay(Guid DoctorId, DateOnly Date, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed class OpenDoctorDayHandler(IDoctorDayScheduleRepository schedules)
{
    public async Task<Guid> HandleAsync(OpenDoctorDay command, CancellationToken cancellationToken)
    {
        var schedule = DoctorDaySchedule.Open(
            new DoctorId(command.DoctorId),
            command.Date,
            command.OpensAt,
            command.ClosesAt);

        schedules.Add(schedule);
        await schedules.SaveChangesAsync(cancellationToken);
        return schedule.Id.Value;
    }
}
