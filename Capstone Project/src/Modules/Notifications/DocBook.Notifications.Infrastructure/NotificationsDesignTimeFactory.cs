using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocBook.Notifications.Infrastructure;

// Keeps the tooling out of the host, which migrates at startup before any migration exists.
public sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(
                Environment.GetEnvironmentVariable("ConnectionStrings__DocBook")
                    ?? "Server=localhost,11434;Database=docbook;Connect Timeout=3",
                sql => sql.MigrationsHistoryTable("__migrations", NotificationsDbContext.Schema))
            .Options;

        return new NotificationsDbContext(options);
    }
}
