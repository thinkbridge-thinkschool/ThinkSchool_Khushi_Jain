namespace DocBook.Scheduling.Domain;

public interface IDoctorDayScheduleRepository
{
    Task<DoctorDaySchedule?> FindAsync(DoctorId doctorId, DateOnly date, CancellationToken cancellationToken);

    Task<DoctorDaySchedule?> FindByAppointmentAsync(AppointmentId appointmentId, CancellationToken cancellationToken);

    void Add(DoctorDaySchedule schedule);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
