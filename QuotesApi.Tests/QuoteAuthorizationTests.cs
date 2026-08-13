using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Contracts;
using QuotesApi.Models;

namespace QuotesApi.Tests;

/// <summary>
/// Exercises the "can-edit-quotes" claim policy and the resource-based
/// ownership check on DELETE /api/quotes/{id} against a real HTTP pipeline,
/// backed by an isolated per-test SQLite file so runs don't share state.
/// </summary>
public sealed class QuoteAuthorizationTests : IAsyncLifetime
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"quotes-tests-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
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
            });

        _client = _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

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

    [Fact]
    public async Task Delete_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.DeleteAsync("/api/quotes/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutWriteScope_ReturnsForbidden()
    {
        var quoteId = await CreateQuoteAsAsync("owner@example.com");

        AuthorizeAs("owner@example.com", scope: null);

        var response = await _client.DeleteAsync($"/api/quotes/{quoteId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task Delete_WithExpiredToken_ReturnsUnauthorized()
    {
        var quoteId = await CreateQuoteAsAsync("owner@example.com");

        AuthorizeAs(
            "owner@example.com",
            "quotes.write",
            DateTime.UtcNow.AddMinutes(-5));

        var response = await _client.DeleteAsync(
            $"/api/quotes/{quoteId}");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithWriteScopeButNotOwner_ReturnsForbidden()
    {
        var quoteId = await CreateQuoteAsAsync("owner@example.com");

        AuthorizeAs("someone-else@example.com", "quotes.write");

        var response = await _client.DeleteAsync($"/api/quotes/{quoteId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithWriteScopeAndOwnership_Succeeds()
    {
        var quoteId = await CreateQuoteAsAsync("owner@example.com");

        AuthorizeAs("owner@example.com", "quotes.write");

        var response = await _client.DeleteAsync($"/api/quotes/{quoteId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private void AuthorizeAs(
        string subject,
        string? scope,
        DateTime? expiresAt = null) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                CreateInternalJwt(subject, scope, expiresAt));
    private async Task<int> CreateQuoteAsAsync(string ownerSubject)
    {
        AuthorizeAs(ownerSubject, "quotes.write");

        var response = await _client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest("Test Author", "Test quote text"));

        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return body.RootElement.GetProperty("id").GetInt32();
    }

    private string CreateInternalJwt(
        string subject,
        string? scope,
        DateTime? expiresAt = null)
    {
        var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

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
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    [Fact]
    public async Task Refresh_ReusingOldRefreshToken_ReturnsUnauthorized()
    {
        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(
                "admin@example.com",
                "P@ssword1"));

        loginResponse.EnsureSuccessStatusCode();

        var login = await loginResponse.Content
            .ReadFromJsonAsync<JsonElement>();

        var refreshToken = login
            .GetProperty("refresh_token")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        // First use: token is rotated.
        var firstRefreshResponse = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(refreshToken!));

        firstRefreshResponse.EnsureSuccessStatusCode();

        // Second use of the OLD token: should be rejected.
        var reuseResponse = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(refreshToken!));

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            reuseResponse.StatusCode);
            }
    }
