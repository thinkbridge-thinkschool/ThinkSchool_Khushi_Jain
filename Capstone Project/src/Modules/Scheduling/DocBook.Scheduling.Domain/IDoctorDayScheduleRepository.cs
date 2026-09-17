namespace DocBook.Scheduling.Domain;

public interface IDoctorDayScheduleRepository
{
    Task<DoctorDaySchedule?> FindAsync(DoctorId doctorId, DateOnly date, CancellationToken cancellationToken);

    // For a caller that only reads: the same day, without the change tracking nobody will save.
    Task<DoctorDaySchedule?> FindForReadingAsync(
        DoctorId doctorId,
        DateOnly date,
        CancellationToken cancellationToken);

    Task<DoctorDaySchedule?> FindByAppointmentAsync(AppointmentId appointmentId, CancellationToken cancellationToken);

    // Paged, because a whole-clinic sweep is otherwise bounded only by how long the clinic has run.
    Task<IReadOnlyList<DoctorDaySchedule>> PageByDateRangeAsync(
        DateOnly from,
        DateOnly to,
        int skip,
        int take,
        CancellationToken cancellationToken);

    void Add(DoctorDaySchedule schedule);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
