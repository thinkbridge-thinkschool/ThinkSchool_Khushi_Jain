using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using QuotesApi.Data;

namespace QuotesApi.Tests.SqlServer;

/// <summary>
/// Used only by the `dotnet ef migrations` CLI to scaffold a SQL-Server-flavored
/// migration set for QuotesDbContext (see SqlServerContainerFixture). The
/// connection string here is a design-time placeholder only -- EF never opens
/// it while scaffolding, and it is not used at test or application runtime,
/// where the real connection string comes from the running Testcontainer.
/// </summary>
public sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<QuotesDbContext>
{
    public QuotesDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<QuotesDbContext>();

        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=QuotesApi;User Id=sa;Password=Placeholder1!;TrustServerCertificate=True;",
            sql => sql.MigrationsAssembly(typeof(SqlServerDesignTimeDbContextFactory).Assembly.FullName));

        return new QuotesDbContext(optionsBuilder.Options);
    }
}
