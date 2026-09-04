using DocBook.Patients.Domain;

namespace DocBook.Patients.Application;

public sealed record RegisterPatient(string FullName, string Email, string Phone);

public sealed class RegisterPatientHandler(IPatientRepository patients, TimeProvider clock)
{
    public async Task<Guid> HandleAsync(RegisterPatient command, CancellationToken cancellationToken)
    {
        var patient = Patient.Register(command.FullName, command.Email, command.Phone, clock.GetUtcNow());

        patients.Add(patient);
        await patients.SaveChangesAsync(cancellationToken);
        return patient.Id.Value;
    }
}
