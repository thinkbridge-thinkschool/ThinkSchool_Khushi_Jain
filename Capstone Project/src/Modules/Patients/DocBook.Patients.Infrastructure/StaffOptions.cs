namespace DocBook.Patients.Infrastructure;

// Clinic staff are not patients and have no aggregate, so the one account comes from configuration.
public sealed class StaffOptions
{
    public const string MissingAccountMessage =
        "Staff:Id, Staff:Email and Staff:PasswordHash are all required. " +
        "Hash the password with Capstone Project/infra/staff-password-hash.cs.";

    public Guid Id { get; init; }

    public string Email { get; init; } = string.Empty;

    public string PasswordHash { get; init; } = string.Empty;

    public bool IsConfigured =>
        Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(PasswordHash);
}
