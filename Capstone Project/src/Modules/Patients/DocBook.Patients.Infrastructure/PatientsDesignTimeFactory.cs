using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocBook.Patients.Infrastructure;

// Keeps the tooling out of the host, which migrates at startup before any migration exists.
public sealed class PatientsDesignTimeFactory : IDesignTimeDbContextFactory<PatientsDbContext>
{
    public PatientsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PatientsDbContext>()
            .UseSqlServer(
                Environment.GetEnvironmentVariable("ConnectionStrings__DocBook")
                    ?? "Server=localhost,11434;Database=docbook;Connect Timeout=3",
                sql => sql.MigrationsHistoryTable("__migrations", PatientsDbContext.Schema))
            .Options;

        return new PatientsDbContext(options);
    }
}
