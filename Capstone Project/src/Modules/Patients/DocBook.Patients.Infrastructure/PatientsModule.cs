using DocBook.Infrastructure;
using DocBook.Patients.Application;
using DocBook.Patients.Contracts;
using DocBook.Patients.Domain;
using DocBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Patients.Infrastructure;

// The module's one entry point into the host.
public static class PatientsModule
{
    public static IServiceCollection AddPatients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PatientsDbContext>(options =>
            options.UseSqlServer(configuration.ConnectionString(), sql =>
            {
                // Its own history table, or the two modules' migrations would fight over one.
                sql.MigrationsHistoryTable("__migrations", PatientsDbContext.Schema);

                // Azure SQL drops connections as a matter of course, and so does a cold container.
                sql.EnableRetryOnFailure();
            }));

        services.Configure<StaffOptions>(configuration.GetSection("Staff"));

        services.AddScoped<RegisterPatientHandler>();
        services.AddScoped<AuthenticatePatientHandler>();

        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IPatientDirectory, PatientDirectory>();

        // Singleton so the dummy hash is computed once rather than per request.
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

        return services;
    }
}
