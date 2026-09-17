using DocBook.Infrastructure;
using DocBook.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Scheduling.Infrastructure;

public sealed class DoctorDayScheduleRepository(
    SchedulingDbContext context,
    DomainEventDispatcher domainEvents) : IDoctorDayScheduleRepository
{
    public Task<DoctorDaySchedule?> FindAsync(
        DoctorId doctorId,
        DateOnly date,
        CancellationToken cancellationToken) =>
        context.Schedules
            .Include(schedule => schedule.Appointments)
            .FirstOrDefaultAsync(
                schedule => schedule.DoctorId == doctorId && schedule.Date == date,
                cancellationToken);

    public Task<DoctorDaySchedule?> FindForReadingAsync(
        DoctorId doctorId,
        DateOnly date,
        CancellationToken cancellationToken) =>
        context.Schedules
            .AsNoTracking()
            .Include(schedule => schedule.Appointments)
            .FirstOrDefaultAsync(
                schedule => schedule.DoctorId == doctorId && schedule.Date == date,
                cancellationToken);

    // Through the navigation: the shadow foreign key is a DoctorDayScheduleId, never a Guid.
    public Task<DoctorDaySchedule?> FindByAppointmentAsync(
        AppointmentId appointmentId,
        CancellationToken cancellationToken) =>
        context.Schedules
            .Include(schedule => schedule.Appointments)
            .FirstOrDefaultAsync(
                schedule => schedule.Appointments.Any(booking => booking.Id == appointmentId),
                cancellationToken);

    public async Task<IReadOnlyList<DoctorDaySchedule>> PageByDateRangeAsync(
        DateOnly from,
        DateOnly to,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        await context.Schedules
            .Include(schedule => schedule.Appointments)
            .Where(schedule => schedule.Date >= from && schedule.Date <= to)
            .OrderBy(schedule => schedule.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Add(DoctorDaySchedule schedule) => context.Schedules.Add(schedule);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var raised = context.ChangeTracker.Entries<DoctorDaySchedule>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .ToList();

        // Marked modified so the root's concurrency check runs even when only a child changed.
        foreach (var entry in raised.Where(entry => entry.State == EntityState.Unchanged))
        {
            entry.State = EntityState.Modified;
        }

        // Before the save, so the outbox rows land in the same transaction as the booking.
        await domainEvents.DispatchAsync(
            raised.Select(entry => entry.Entity),
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
    }
}
