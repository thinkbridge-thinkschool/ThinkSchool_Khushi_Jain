using DocBook.SharedKernel;

namespace DocBook.Patients.Domain;

public readonly record struct PatientId(Guid Value)
{
    public static PatientId New() => new(Guid.CreateVersion7());
}

public sealed class Patient : AggregateRoot<PatientId>
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 256;
    public const int MaxPhoneLength = 32;

    private Patient(
        PatientId id,
        string fullName,
        string email,
        string phone,
        string passwordHash,
        DateTimeOffset registeredAt)
        : base(id)
    {
        FullName = fullName;
        Email = email;
        Phone = phone;
        PasswordHash = passwordHash;
        RegisteredAt = registeredAt;
    }

    private Patient()
    {
    }

    public string FullName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string Phone { get; private set; } = string.Empty;

    // Never leaves the Patients module. `IPatientDirectory` has no way to reach it.
    public string PasswordHash { get; private set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; private set; }

    public static Patient Register(
        string fullName,
        string email,
        string phone,
        string passwordHash,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > MaxNameLength)
        {
            throw new DomainException("name_required", "A patient needs a name.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > MaxEmailLength)
        {
            throw new DomainException("email_required", "A patient needs an email address we can reach them on.");
        }

        if (phone.Length > MaxPhoneLength)
        {
            throw new DomainException("phone_too_long", "That phone number is too long.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("password_required", "A patient needs a password.");
        }

        return new Patient(PatientId.New(), fullName.Trim(), email.Trim().ToLowerInvariant(), phone.Trim(), passwordHash, now);
    }
}
