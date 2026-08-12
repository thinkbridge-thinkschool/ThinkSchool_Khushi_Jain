using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}"
                    });
                });
            });

        _client = _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

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

    private void AuthorizeAs(string subject, string? scope) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateInternalJwt(subject, scope));

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

    private string CreateInternalJwt(string subject, string? scope)
    {
        var jwtSettings = _factory.Services.GetRequiredService<JwtSettings>();

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
            issuer: jwtSettings.Issuer,
            audience: jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
