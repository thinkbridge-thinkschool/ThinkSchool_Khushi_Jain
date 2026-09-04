namespace DocBook.Patients.Domain;

public interface IPatientRepository
{
    Task<Patient?> FindAsync(PatientId patientId, CancellationToken cancellationToken);

    void Add(Patient patient);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
