using DocBook.Patients.Domain;

namespace DocBook.Application.Tests;

internal sealed class FakePatientRepository : IPatientRepository
{
    private readonly List<Patient> _patients = [];

    public FakePatientRepository(params Patient[] seeded) => _patients.AddRange(seeded);

    public IReadOnlyList<Patient> Patients => _patients;

    public int SaveCount { get; private set; }

    public Task<Patient?> FindAsync(PatientId patientId, CancellationToken cancellationToken) =>
        Task.FromResult(_patients.FirstOrDefault(patient => patient.Id == patientId));

    public Task<Patient?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(_patients.FirstOrDefault(patient => patient.Email == email));

    public void Add(Patient patient) => _patients.Add(patient);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

// Reversible on purpose, so a test can tell a stored hash from the password behind it.
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public string UnknownAccountHash => "hash:unknown-account";

    public List<string> Verified { get; } = [];

    public string Hash(string password) => $"hash:{password}";

    public bool Verify(string password, string hash)
    {
        Verified.Add(hash);
        return hash == Hash(password);
    }
}
