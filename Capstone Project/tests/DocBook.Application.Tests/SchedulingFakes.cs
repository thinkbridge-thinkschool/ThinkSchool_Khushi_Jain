using DocBook.Scheduling.Domain;

namespace DocBook.Application.Tests;

internal sealed class FakeScheduleRepository : IDoctorDayScheduleRepository
{
    private readonly List<DoctorDaySchedule> _schedules = [];

    public FakeScheduleRepository(params DoctorDaySchedule[] seeded) => _schedules.AddRange(seeded);

    public IReadOnlyList<DoctorDaySchedule> Schedules => _schedules;

    public int SaveCount { get; private set; }

    public int FindCalls { get; private set; }

    public DateOnly? PagedFrom { get; private set; }

    public DateOnly? PagedTo { get; private set; }

    public Task<DoctorDaySchedule?> FindAsync(
        DoctorId doctorId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        FindCalls++;

        return Task.FromResult(_schedules
            .FirstOrDefault(schedule => schedule.DoctorId == doctorId && schedule.Date == date));
    }

    public Task<DoctorDaySchedule?> FindByAppointmentAsync(
        AppointmentId appointmentId,
        CancellationToken cancellationToken)
    {
        FindCalls++;

        return Task.FromResult(_schedules
            .FirstOrDefault(schedule => schedule.Appointments.Any(booking => booking.Id == appointmentId)));
    }

    public Task<IReadOnlyList<DoctorDaySchedule>> PageByDateRangeAsync(
        DateOnly from,
        DateOnly to,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        PagedFrom = from;
        PagedTo = to;

        IReadOnlyList<DoctorDaySchedule> page = _schedules
            .Where(schedule => schedule.Date >= from && schedule.Date <= to)
            .OrderBy(schedule => schedule.Id.Value)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(page);
    }

    public void Add(DoctorDaySchedule schedule) => _schedules.Add(schedule);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
