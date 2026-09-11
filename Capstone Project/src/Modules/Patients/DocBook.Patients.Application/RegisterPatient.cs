using DocBook.Patients.Domain;

namespace DocBook.Patients.Application;

public sealed record RegisterPatient(string FullName, string Email, string Phone, string Password);

public enum RegistrationOutcome
{
    Registered = 1,
    EmailAlreadyRegistered = 2,
}

public sealed record RegistrationResult(RegistrationOutcome Outcome, Guid PatientId);

public sealed class RegisterPatientHandler(
    IPatientRepository patients,
    IPasswordHasher passwords,
    TimeProvider clock)
{
    public async Task<RegistrationResult> HandleAsync(RegisterPatient command, CancellationToken cancellationToken)
    {
        var email = command.Email.Trim().ToLowerInvariant();

        if (await patients.FindByEmailAsync(email, cancellationToken) is not null)
        {
            return new RegistrationResult(RegistrationOutcome.EmailAlreadyRegistered, Guid.Empty);
        }

        var patient = Patient.Register(
            command.FullName,
            email,
            command.Phone,
            passwords.Hash(command.Password),
            clock.GetUtcNow());

        patients.Add(patient);
        await patients.SaveChangesAsync(cancellationToken);

        return new RegistrationResult(RegistrationOutcome.Registered, patient.Id.Value);
    }
}
