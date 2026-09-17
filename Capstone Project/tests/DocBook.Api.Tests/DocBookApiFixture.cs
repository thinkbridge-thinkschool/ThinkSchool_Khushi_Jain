using DocBook.Api.Extensions;
using DocBook.Infrastructure;
using DocBook.Patients.Infrastructure;
using DocBook.Scheduling.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;

namespace DocBook.Api.Tests;

// One SQL Server for the whole collection, hosting the real pipeline that Program.cs builds.
public sealed class DocBookApiFixture : IAsyncLifetime
{
    public const string StaffEmail = "desk@docbook.test";
    public const string StaffPassword = "clinic-desk-password";
    public const string SigningKey = "docbook-integration-tests-signing-key";

    public static readonly Guid StaffId = new("4f2c9a17-6b3e-4d81-9c05-2ae7f1b8d640");

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "docbook"
        }.ConnectionString;

        Factory = CreateHost();

        // Starting the host is what applies the three migration sets, so it happens once here.
        Factory.CreateClient().Dispose();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    // A host of its own, for a test that needs a setting the shared one deliberately relaxes.
    public WebApplicationFactory<Program> CreateHost(params (string Key, string Value)[] settings) =>
        Build(settings, services =>
        {
            // Both are timers, and a timer firing mid-test is a test that fails on a slow machine.
            services.RemoveHostedService<OutboxDispatcher>();
            services.RemoveHostedService<ReminderSweepService>();
        });

    // The dispatcher left running, which is the only way the async path can be watched end to end.
    public WebApplicationFactory<Program> CreateDispatchingHost(Action<IServiceCollection> configure) =>
        Build(
            [("Outbox:PollInterval", "00:00:01"), ("Outbox:ClaimDuration", "00:00:05")],
            services =>
            {
                services.RemoveHostedService<ReminderSweepService>();
                configure(services);
            });

    private WebApplicationFactory<Program> Build(
        (string Key, string Value)[] settings,
        Action<IServiceCollection> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            builder.UseSetting($"ConnectionStrings:{DatabaseConfiguration.ConnectionName}", ConnectionString);
            builder.UseSetting("Jwt:SigningKey", SigningKey);
            builder.UseSetting("Staff:Id", StaffId.ToString());
            builder.UseSetting("Staff:Email", StaffEmail);
            builder.UseSetting("Staff:PasswordHash", new BCryptPasswordHasher().Hash(StaffPassword));

            // Five a minute is per address, and every test in the run shares one address.
            builder.UseSetting($"{RateLimitOptions.Section}:SensitivePerMinute", "10000");

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(configure);
        });
}

internal static class HostedServiceExtensions
{
    public static void RemoveHostedService<THostedService>(this IServiceCollection services)
        where THostedService : IHostedService
    {
        var registered = services
            .Where(service =>
                service.ServiceType == typeof(IHostedService) &&
                service.ImplementationType == typeof(THostedService))
            .ToList();

        foreach (var descriptor in registered)
        {
            services.Remove(descriptor);
        }
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<DocBookApiFixture>;
