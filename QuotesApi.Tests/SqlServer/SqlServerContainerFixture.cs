using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Data;
using QuotesApi.Time;
using Testcontainers.MsSql;

namespace QuotesApi.Tests.SqlServer;

/// <summary>
/// Starts one real SQL Server 2022 container (via Testcontainers) for the
/// lifetime of a test class, hosts the actual QuotesApi pipeline against it
/// through WebApplicationFactory, and lets EF Core apply the SQL-Server
/// migration set (see SqlServer/Migrations) exactly like the production
/// startup path does for SQLite. Shared across all tests in the
/// SqlServerCollection via ICollectionFixture -- starting a fresh container
/// per test would be prohibitively slow, so individual tests seed and assert
/// only their own uniquely-named data rather than assuming an empty database.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public FakeClock Clock { get; } = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");

                builder.UseTestSecrets();

                builder.ConfigureTestServices(services =>
                {
                    // AddInfrastructure already registered QuotesDbContext against Sqlite.
                    // Removing DbContextOptions<QuotesDbContext> alone leaves that
                    // registration's other EF Core services behind, so EF Core then
                    // sees two database providers registered and throws. Strip every
                    // EF Core service the Sqlite registration added before adding the
                    // SqlServer-backed context.
                    services.RemoveAll<DbContextOptions<QuotesDbContext>>();
                    services.RemoveAll<QuotesDbContext>();

                    foreach (var descriptor in services
                        .Where(d => d.ServiceType.Namespace?.StartsWith("Microsoft.EntityFrameworkCore") == true)
                        .ToList())
                    {
                        services.Remove(descriptor);
                    }

                    services.AddDbContext<QuotesDbContext>(options =>
                        options.UseSqlServer(
                            _container.GetConnectionString(),
                            sql => sql.MigrationsAssembly(
                                typeof(SqlServerDesignTimeDbContextFactory).Assembly.FullName)));

                    services.AddSingleton<IClock>(Clock);
                });
            });

        // Accessing Services forces WebApplicationFactory to build and start
        // the host now (rather than lazily on the first test's request),
        // which runs MigrateAndSeedAsync -- EF migrations and the
        // admin user seed -- against the container we just started, using
        // the exact same production code path as the SQLite host.
        _ = Factory.Services;
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        await _container.DisposeAsync();
    }
}
