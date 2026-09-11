using DocBook.Patients.Contracts;
using DocBook.Patients.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Patients.Infrastructure;

public sealed class PatientRepository(PatientsDbContext context) : IPatientRepository
{
    public Task<Patient?> FindAsync(PatientId patientId, CancellationToken cancellationToken) =>
        context.Patients.FirstOrDefaultAsync(patient => patient.Id == patientId, cancellationToken);

    public Task<Patient?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        context.Patients.FirstOrDefaultAsync(patient => patient.Email == email, cancellationToken);

    public void Add(Patient patient) => context.Patients.Add(patient);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}

// The whole of what another module may learn about a patient: no phone number, no password hash.
public sealed class PatientDirectory(PatientsDbContext context) : IPatientDirectory
{
    public async Task<PatientContact?> FindAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var id = new PatientId(patientId);

        return await context.Patients
            .Where(patient => patient.Id == id)
            .Select(patient => new PatientContact(patientId, patient.FullName, patient.Email))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    // Computed once from a value nobody holds, so it can never match a password anyone types.
    public string UnknownAccountHash { get; } =
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
