using DocBook.SharedKernel;

namespace DocBook.Patients.Domain.Tests;

public class PatientTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_keeps_what_it_was_given()
    {
        var patient = Register();

        Assert.Equal("Ada Lovelace", patient.FullName);
        Assert.Equal("ada@example.com", patient.Email);
        Assert.Equal("0000000000", patient.Phone);
        Assert.Equal("hashed", patient.PasswordHash);
        Assert.Equal(Now, patient.RegisteredAt);
    }

    // Sign-in looks the address up as it was typed, so a stored address that is not folded never matches.
    [Fact]
    public void Register_folds_the_address_to_lower_case_and_trims_it()
    {
        var patient = Register(email: "  Ada@Example.COM  ");

        Assert.Equal("ada@example.com", patient.Email);
    }

    [Fact]
    public void Register_trims_the_name_and_the_phone()
    {
        var patient = Register(fullName: "  Ada Lovelace  ", phone: "  0000000000  ");

        Assert.Equal("Ada Lovelace", patient.FullName);
        Assert.Equal("0000000000", patient.Phone);
    }

    [Fact]
    public void Register_gives_every_patient_their_own_id()
    {
        Assert.NotEqual(Register().Id, Register().Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_missing_name(string fullName)
    {
        var error = Assert.Throws<DomainException>(() => Register(fullName: fullName));

        Assert.Equal("name_required", error.Code);
    }

    [Fact]
    public void Register_rejects_a_name_past_the_length_limit()
    {
        var error = Assert.Throws<DomainException>(() =>
            Register(fullName: new string('a', Patient.MaxNameLength + 1)));

        Assert.Equal("name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ada.example.com")]
    public void Register_rejects_an_address_it_cannot_reach(string email)
    {
        var error = Assert.Throws<DomainException>(() => Register(email: email));

        Assert.Equal("email_required", error.Code);
    }

    [Fact]
    public void Register_rejects_an_address_past_the_length_limit()
    {
        var local = new string('a', Patient.MaxEmailLength);

        var error = Assert.Throws<DomainException>(() => Register(email: $"{local}@example.com"));

        Assert.Equal("email_required", error.Code);
    }

    [Fact]
    public void Register_rejects_a_phone_past_the_length_limit()
    {
        var error = Assert.Throws<DomainException>(() =>
            Register(phone: new string('0', Patient.MaxPhoneLength + 1)));

        Assert.Equal("phone_too_long", error.Code);
    }

    [Fact]
    public void Register_rejects_a_missing_password()
    {
        var error = Assert.Throws<DomainException>(() => Register(passwordHash: "   "));

        Assert.Equal("password_required", error.Code);
    }

    private static Patient Register(
        string fullName = "Ada Lovelace",
        string email = "ada@example.com",
        string phone = "0000000000",
        string passwordHash = "hashed") =>
        Patient.Register(fullName, email, phone, passwordHash, Now);
}
