using DocBook.Patients.Application;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Patients.Infrastructure;

// The module's one entry point into the host. Its adapters — EF Core mapping, the repository,
// the IPatientDirectory implementation and the endpoints — are registered here as they are built.
public static class PatientsModule
{
    public static IServiceCollection AddPatients(this IServiceCollection services)
    {
        services.AddScoped<RegisterPatientHandler>();

        return services;
    }
}
