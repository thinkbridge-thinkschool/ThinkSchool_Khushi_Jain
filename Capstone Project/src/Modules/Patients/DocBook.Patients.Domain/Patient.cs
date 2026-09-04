using DocBook.SharedKernel;

namespace DocBook.Patients.Domain;

public readonly record struct PatientId(Guid Value)
{
    public static PatientId New() => new(Guid.CreateVersion7());
}

public sealed class Patient : AggregateRoot<PatientId>
{
    private Patient(PatientId id, string fullName, string email, string phone, DateTimeOffset registeredAt)
        : base(id)
    {
        FullName = fullName;
        Email = email;
        Phone = phone;
        RegisteredAt = registeredAt;
    }

    private Patient()
    {
    }

    public string FullName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string Phone { get; private set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; private set; }

    public static Patient Register(string fullName, string email, string phone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new DomainException("A patient needs a name.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            throw new DomainException("A patient needs an email address we can reach them on.");
        }

        return new Patient(PatientId.New(), fullName.Trim(), email.Trim(), phone.Trim(), now);
    }
}
