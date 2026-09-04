namespace DocBook.Patients.Contracts;

public sealed record PatientContact(Guid PatientId, string FullName, string Email);

// The only way another module may read a patient. Read-only, and never the full record.
public interface IPatientDirectory
{
    Task<PatientContact?> FindAsync(Guid patientId, CancellationToken cancellationToken);
}
