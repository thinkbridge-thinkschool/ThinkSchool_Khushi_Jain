using DocBook.Patients.Domain;

namespace DocBook.Patients.Application;

public sealed record AuthenticatePatient(string Email, string Password);

public sealed class AuthenticatePatientHandler(IPatientRepository patients, IPasswordHasher passwords)
{
    public async Task<Guid?> HandleAsync(AuthenticatePatient command, CancellationToken cancellationToken)
    {
        var patient = await patients.FindByEmailAsync(
            command.Email.Trim().ToLowerInvariant(),
            cancellationToken);

        if (patient is null)
        {
            passwords.Verify(command.Password, passwords.UnknownAccountHash);
            return null;
        }

        return passwords.Verify(command.Password, patient.PasswordHash)
            ? patient.Id.Value
            : null;
    }
}
