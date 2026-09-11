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

        var rows = await context.Appointments
            .Where(appointment => appointment.PatientId == owner && appointment.Slot.Start >= from)
            .OrderBy(appointment => appointment.Slot.Start)
            .Skip(skip)
            .Take(take)
            .Select(appointment => new
            {
                appointment.Id,
                appointment.Slot,
                appointment.Status,
                appointment.Reason,
                ScheduleId = EF.Property<Guid>(appointment, "ScheduleId")
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var scheduleIds = rows
            .Select(row => new DoctorDayScheduleId(row.ScheduleId))
            .Distinct()
            .ToList();

        var doctors = await context.Schedules
            .Where(schedule => scheduleIds.Contains(schedule.Id))
            .Select(schedule => new { schedule.Id, schedule.DoctorId })
            .ToListAsync(cancellationToken);

        var doctorBySchedule = doctors.ToDictionary(row => row.Id.Value, row => row.DoctorId.Value);

        return rows
            .Select(row => new PatientAppointment(
                row.Id.Value,
                doctorBySchedule.GetValueOrDefault(row.ScheduleId),
                row.Slot.Start,
                row.Slot.End,
                row.Status.ToString(),
                row.Reason))
            .ToList();
    }
}
