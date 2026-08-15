using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Models;
using QuotesApi.Time;

namespace QuotesApi.Tests;

/// <summary>
/// Hosts the real QuotesApi pipeline via WebApplicationFactory against an
/// isolated, per-instance SQLite file, with IClock swapped for a FakeClock.
/// xUnit creates a fresh instance of any derived test class per test method,
/// so each test gets its own factory, database file, HttpClient, and clock.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"quotes-integration-{Guid.NewGuid():N}.db");

    protected string DbPath => _dbPath;

    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected FakeClock Clock { get; } = new();

    public Task InitializeAsync()
    {
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");

                // Program.cs reads ConnectionStrings:DefaultConnection before
                // builder.Build() runs, so ConfigureAppConfiguration (which only
                // merges at Build() time) arrives too late to affect it.
                // UseSetting is folded into configuration immediately.
                builder.UseSetting(
                    "ConnectionStrings:DefaultConnection",
                    $"Data Source={_dbPath}");

                builder.UseTestSecrets();

                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IClock>(Clock);
                });
            });

        Client = Factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();

        // Microsoft.Data.Sqlite pools connections at the process level, which
        // keeps the file locked even after the factory (and its DbContexts)
        // are disposed. Clear the pool so the temp file can be deleted.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        return Task.CompletedTask;
    }

    protected void AuthorizeAs(string subject, string? scope = "quotes.write") =>
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateInternalJwt(subject, scope));

    protected string CreateInternalJwt(
        string subject,
        string? scope = "quotes.write",
        DateTime? expiresAt = null)
    {
        var jwtOptions = Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: expiresAt ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
