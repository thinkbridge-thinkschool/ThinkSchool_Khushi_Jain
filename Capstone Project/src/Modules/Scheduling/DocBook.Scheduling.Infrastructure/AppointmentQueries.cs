using DocBook.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Scheduling.Infrastructure;

public sealed record PatientAppointment(
    Guid AppointmentId,
    Guid DoctorId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Status,
    string Reason);

// The read side: many schedules per answer, so it queries tables rather than loading aggregates.
public sealed class AppointmentQueries(SchedulingDbContext context, TimeProvider clock)
{
    public async Task<IReadOnlyList<PatientAppointment>> ForPatientAsync(
        Guid patientId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var owner = new PatientId(patientId);
        var from = clock.GetUtcNow();

        // The doctor is on the schedule row, so the answer is one join rather than a second query.
        var rows = await context.Schedules
            .SelectMany(
                schedule => schedule.Appointments,
                (schedule, appointment) => new
                {
                    schedule.DoctorId,
                    appointment.Id,
                    appointment.PatientId,
                    Start = appointment.Slot.Start,
                    End = appointment.Slot.End,
                    appointment.Status,
                    appointment.Reason
                })
            .Where(row => row.PatientId == owner && row.Start >= from)
            .OrderBy(row => row.Start)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new PatientAppointment(
                row.Id.Value,
                row.DoctorId.Value,
                row.Start,
                row.End,
                row.Status.ToString(),
                row.Reason))
            .ToList();
    }
}
