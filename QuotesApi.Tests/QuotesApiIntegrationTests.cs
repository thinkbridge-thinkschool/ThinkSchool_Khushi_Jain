using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Contracts;
using QuotesApi.Data;

namespace QuotesApi.Tests;

/// <summary>
/// End-to-end tests against the real WebApplicationFactory pipeline, covering
/// every mapped endpoint's success and failure paths not already exercised by
/// QuoteAuthorizationTests (token-issuer routing, scope policy, ownership).
/// </summary>
public sealed class QuotesApiIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOkWithTokens()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(
                TestConfiguration.SeedAdminEmail,
                TestConfiguration.SeedAdminPassword));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(TestConfiguration.SeedAdminEmail, "wrong-password"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("nobody@example.com", "whatever"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndReturnsNewTokens()
    {
        var originalRefreshToken = await LoginAndGetRefreshTokenAsync();

        var response = await Client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(originalRefreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("refresh_token").GetString().Should().NotBe(originalRefreshToken);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest("this-token-was-never-issued"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_AfterClockAdvancesPastExpiry_ReturnsUnauthorized()
    {
        var refreshToken = await LoginAndGetRefreshTokenAsync();

        // Refresh tokens are issued with a 7-day lifetime. Advance the
        // substituted IClock instead of waiting in real time.
        Clock.UtcNow = DateTimeOffset.UtcNow.AddDays(8);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(refreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ThenRefresh_ReturnsUnauthorized()
    {
        var refreshToken = await LoginAndGetRefreshTokenAsync();

        var logoutResponse = await Client.PostAsJsonAsync(
            "/api/auth/logout",
            new RefreshTokenRequest(refreshToken));

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshResponse = await Client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(refreshToken));

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithUnknownToken_ReturnsNoContent()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/logout",
            new RefreshTokenRequest("this-token-was-never-issued"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetQuotes_OnFreshDatabase_ReturnsEmptyPagedResult()
    {
        // No arrangement: proves each test starts against an isolated,
        // empty database rather than state left over by another test.
        var response = await Client.GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetQuotes_WithInvalidPageSize_ReturnsValidationProblem()
    {
        var response = await Client.GetAsync("/api/quotes?size=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("page/size");
    }

    [Fact]
    public async Task GetQuoteById_WhenNotFound_ReturnsNotFound()
    {
        var response = await Client.GetAsync("/api/quotes/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetQuoteById_WhenFound_ReturnsMatchingQuote()
    {
        var quoteId = await CreateQuoteAsync("owner@example.com", "Seneca", "Luck is what happens when preparation meets opportunity.");

        var response = await Client.GetAsync($"/api/quotes/{quoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("author").GetString().Should().Be("Seneca");
        body.GetProperty("text").GetString().Should().Be("Luck is what happens when preparation meets opportunity.");
    }

    [Fact]
    public async Task CreateQuote_WithValidRequestAndWriteScope_ReturnsCreatedWithQuoteBody()
    {
        AuthorizeAs("owner@example.com", "quotes.write");

        var response = await Client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest("Epictetus", "It's not what happens to you, but how you react to it."));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().StartWith("/api/quotes/");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("author").GetString().Should().Be("Epictetus");
        body.GetProperty("ownerId").GetString().Should().Be("owner@example.com");
    }

    [Fact]
    public async Task CreateQuote_WithoutToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest("Author", "Text"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The request record's DataAnnotations are evaluated before the handler
    /// runs, so this never reaches Quote.Create. The response must be a
    /// ValidationProblemDetails -- an "errors" dictionary keyed by member name
    /// -- rather than the flat ProblemDetails the domain layer produces.
    /// </summary>
    [Fact]
    public async Task CreateQuote_WithEmptyAuthor_ReturnsValidationProblemDetails()
    {
        AuthorizeAs("owner@example.com", "quotes.write");

        var response = await Client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest("", "Some valid text"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("errors", out var errors)
            .Should().BeTrue("ValidationProblemDetails carries a keyed errors dictionary");

        errors.TryGetProperty(nameof(CreateQuoteRequest.Author), out _)
            .Should().BeTrue("the failing member should be named in the response");
    }

    /// <summary>
    /// Author length is an invariant the aggregate owns as well as an
    /// annotation, so this covers the domain path still returning 400 for a
    /// value that gets past binding.
    /// </summary>
    [Fact]
    public async Task CreateQuote_WithAuthorOver200Characters_ReturnsBadRequest()
    {
        AuthorizeAs("owner@example.com", "quotes.write");

        var response = await Client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest(new string('a', 201), "Some valid text"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteQuote_WhenQuoteDoesNotExist_ReturnsNotFound()
    {
        AuthorizeAs("owner@example.com", "quotes.write");

        var response = await Client.DeleteAsync("/api/quotes/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Migrations_AppliedToTestDatabase_IncludeAllExpectedMigrations()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();

        appliedMigrations.Should().Contain(new[]
        {
            "20260810090959_InitialCreate",
            "20260811073134_AddUsersTable",
            "20260811092056_AddRefreshTokens",
            "20260812063836_AddQuoteOwner"
        });
    }

    private async Task<string> LoginAndGetRefreshTokenAsync()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(
                TestConfiguration.SeedAdminEmail,
                TestConfiguration.SeedAdminPassword));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("refresh_token").GetString()!;
    }

    private async Task<int> CreateQuoteAsync(string ownerSubject, string author, string text)
    {
        AuthorizeAs(ownerSubject, "quotes.write");

        var response = await Client.PostAsJsonAsync(
            "/api/quotes",
            new CreateQuoteRequest(author, text));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }
}
