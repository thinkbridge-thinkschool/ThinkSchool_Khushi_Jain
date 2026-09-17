using DocBook.Patients.Application;
using DocBook.Patients.Domain;

namespace DocBook.Application.Tests;

public class AuthenticatePatientHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly FakePasswordHasher _passwords = new();
    private readonly FakePatientRepository _patients;
    private readonly Patient _ada;

    public AuthenticatePatientHandlerTests()
    {
        _ada = Patient.Register("Ada Lovelace", "ada@example.com", "0000000000", "hash:correct horse", Now);
        _patients = new FakePatientRepository(_ada);
    }

    [Fact]
    public async Task A_matching_password_answers_with_the_patient_id()
    {
        var id = await Handle("ada@example.com", "correct horse");

        Assert.Equal(_ada.Id.Value, id);
    }

    [Fact]
    public async Task A_wrong_password_answers_with_nothing()
    {
        Assert.Null(await Handle("ada@example.com", "guess"));
    }

    [Fact]
    public async Task The_address_is_matched_folded_to_lower_case()
    {
        var id = await Handle("  Ada@Example.COM  ", "correct horse");

        Assert.Equal(_ada.Id.Value, id);
    }

    // An address nobody holds must cost what a wrong password costs, or sign-in lists who is registered.
    [Fact]
    public async Task An_unknown_address_still_verifies_a_hash()
    {
        var id = await Handle("nobody@example.com", "correct horse");

        Assert.Null(id);
        Assert.Equal(_passwords.UnknownAccountHash, Assert.Single(_passwords.Verified));
    }

    private Task<Guid?> Handle(string email, string password) =>
        new AuthenticatePatientHandler(_patients, _passwords).HandleAsync(
            new AuthenticatePatient(email, password),
            CancellationToken.None);
}
