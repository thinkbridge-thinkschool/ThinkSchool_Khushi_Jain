using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocBook.Scheduling.Infrastructure;

// Keeps the tooling out of the host, which migrates at startup before any migration exists.
public sealed class SchedulingDesignTimeFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlServer(
                Environment.GetEnvironmentVariable("ConnectionStrings__DocBook")
                    ?? "Server=localhost,11434;Database=docbook;Connect Timeout=3",
                sql => sql.MigrationsHistoryTable("__migrations", SchedulingDbContext.Schema))
            .Options;

        return new SchedulingDbContext(options);
    }
}
