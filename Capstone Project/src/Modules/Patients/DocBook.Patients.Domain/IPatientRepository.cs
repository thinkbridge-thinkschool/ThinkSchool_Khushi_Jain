namespace DocBook.Patients.Domain;

public interface IPatientRepository
{
    Task<Patient?> FindAsync(PatientId patientId, CancellationToken cancellationToken);

    Task<Patient?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    void Add(Patient patient);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

// A port, so the module names what it needs and the adapter picks the algorithm.
public interface IPasswordHasher
{
    // A hash of a value nobody knows, so an unknown address costs the same time as a wrong password.
    string UnknownAccountHash { get; }

    string Hash(string password);

    bool Verify(string password, string hash);
}
