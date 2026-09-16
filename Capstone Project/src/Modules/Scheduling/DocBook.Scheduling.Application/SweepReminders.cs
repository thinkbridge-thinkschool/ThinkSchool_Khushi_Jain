using DocBook.Scheduling.Domain;

namespace DocBook.Scheduling.Application;

// A page per unit of work, so an interrupted sweep keeps what it has already stamped.
public sealed class SweepRemindersHandler(IDoctorDayScheduleRepository schedules, TimeProvider clock)
{
    public async Task<int> HandleAsync(
        TimeSpan leadTime,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // The whole range, not the far end of it: an appointment booked for this afternoon is due
        // a reminder now, and a sweep that only ever loads one date never hands it to the aggregate.
        var from = DateOnly.FromDateTime(now.UtcDateTime);
        var to = DateOnly.FromDateTime(now.Add(leadTime).UtcDateTime);

        var swept = 0;
        var skip = 0;

        while (true)
        {
            var page = await schedules.PageByDateRangeAsync(from, to, skip, pageSize, cancellationToken);

            if (page.Count == 0)
            {
                return swept;
            }

            foreach (var schedule in page)
            {
                schedule.MarkRemindersDue(now, leadTime);
            }

            await schedules.SaveChangesAsync(cancellationToken);

            swept += page.Count;
            skip += page.Count;
        }
    }
}
