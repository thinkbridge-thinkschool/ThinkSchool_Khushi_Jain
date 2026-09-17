using DocBook.Patients.Application;
using DocBook.Patients.Domain;

namespace DocBook.Application.Tests;

public class RegisterPatientHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly FakePatientRepository _patients = new();
    private readonly FakePasswordHasher _passwords = new();

    [Fact]
    public async Task Registering_stores_the_patient()
    {
        var result = await Handle("ada@example.com");

        Assert.Equal(RegistrationOutcome.Registered, result.Outcome);
        Assert.NotEqual(Guid.Empty, result.PatientId);
        Assert.Equal(1, _patients.SaveCount);
        Assert.Equal("ada@example.com", Assert.Single(_patients.Patients).Email);
    }

    [Fact]
    public async Task The_password_is_hashed_before_it_is_stored()
    {
        await Handle("ada@example.com");

        var stored = Assert.Single(_patients.Patients).PasswordHash;
        Assert.NotEqual("correct horse", stored);
        Assert.Equal(_passwords.Hash("correct horse"), stored);
    }

    // Registration answers the same way either way, so this outcome never reaches the caller.
    [Fact]
    public async Task An_address_already_registered_creates_nothing()
    {
        await Handle("ada@example.com");

        var result = await Handle("ada@example.com");

        Assert.Equal(RegistrationOutcome.EmailAlreadyRegistered, result.Outcome);
        Assert.Equal(Guid.Empty, result.PatientId);
        Assert.Single(_patients.Patients);
        Assert.Equal(1, _patients.SaveCount);
    }

    [Fact]
    public async Task The_address_is_matched_folded_to_lower_case()
    {
        await Handle("ada@example.com");

        var result = await Handle("  Ada@Example.COM  ");

        Assert.Equal(RegistrationOutcome.EmailAlreadyRegistered, result.Outcome);
    }

    private Task<RegistrationResult> Handle(string email) =>
        new RegisterPatientHandler(_patients, _passwords, new FixedClock(Now)).HandleAsync(
            new RegisterPatient("Ada Lovelace", email, "0000000000", "correct horse"),
            CancellationToken.None);
}
