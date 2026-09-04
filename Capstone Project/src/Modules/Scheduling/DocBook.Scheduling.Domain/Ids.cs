namespace DocBook.Scheduling.Domain;

public readonly record struct DoctorId(Guid Value);

// Scheduling only needs a reference to a patient. The record behind it belongs to the Patients module.
public readonly record struct PatientId(Guid Value);

public readonly record struct AppointmentId(Guid Value)
{
    public static AppointmentId New() => new(Guid.CreateVersion7());
}

public readonly record struct DoctorDayScheduleId(Guid Value)
{
    public static DoctorDayScheduleId New() => new(Guid.CreateVersion7());
}
