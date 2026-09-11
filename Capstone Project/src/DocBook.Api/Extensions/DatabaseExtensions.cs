using DocBook.Patients.Infrastructure;
using DocBook.Scheduling.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Api.Extensions;

public static class DatabaseExtensions
{
    // One database, one migration set per module, each with its own history table in its own schema.
    public static async Task MigrateAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<PatientsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().Database.MigrateAsync();
    }
}
