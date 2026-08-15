using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Tests.SqlServer;

/// <summary>
/// Proves the real QuotesApi pipeline behaves the same way against a genuine
/// SQL Server 2022 container as it does against SQLite in
/// QuotesApiIntegrationTests. The container and its applied migrations are
/// shared across these tests (see SqlServerContainerFixture); each test uses
/// a unique subject/author per run so tests don't depend on each other or on
/// execution order.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class QuotesApiSqlServerIntegrationTests(SqlServerContainerFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Migrations_AppliedToSqlServerContainer_IncludeInitialCreate()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();

        appliedMigrations.Should().Contain(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_WithSeededAdminAccount_ReturnsOkWithTokens()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.com", "P@ssword1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateQuote_WithWriteScope_PersistsToSqlServerAndIsRetrievable()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        AuthorizeAs(owner);

        var createResponse = await _client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest($"Author-{Guid.NewGuid():N}", "Persisted via a real SQL Server container."));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = created.GetProperty("id").GetInt32();

        var getResponse = await _client.GetAsync($"/api/quotes/{quoteId}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        fetched.GetProperty("ownerId").GetString().Should().Be(owner);
    }

    [Fact]
    public async Task DeleteQuote_ByNonOwner_ReturnsForbidden()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        AuthorizeAs(owner);

        var createResponse = await _client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest($"Author-{Guid.NewGuid():N}", "Only the owner may delete this."));

        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = created.GetProperty("id").GetInt32();

        AuthorizeAs($"someone-else-{Guid.NewGuid():N}@example.com");

        var deleteResponse = await _client.DeleteAsync($"/api/quotes/{quoteId}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateQuote_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest("Author", "Text"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private void AuthorizeAs(string subject) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateInternalJwt(subject));

    private string CreateInternalJwt(string subject)
    {
        var jwtOptions = fixture.Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("scope", "quotes.write")
        };

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// Groups the SQL Server tests into one xUnit collection so they share a
/// single SqlServerContainerFixture instance (one container, one migration
/// run) instead of xUnit creating a new fixture -- and a new container --
/// per test class.
/// </summary>
[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>;
