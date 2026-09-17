using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DocBook.Api.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace DocBook.Api.Tests;

[Collection(nameof(ApiCollection))]
public class AuthenticationApiTests(DocBookApiFixture fixture)
{
    [Fact]
    public async Task Health_is_the_one_route_that_needs_no_token()
    {
        var response = await fixture.Factory.Anonymous().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_route_without_a_token_is_refused()
    {
        var response = await fixture.Factory.Anonymous().GetAsync("/api/v1/appointments/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The document describes every route in one response, so reading it is not anonymous either.
    [Fact]
    public async Task The_openapi_document_needs_a_token()
    {
        var response = await fixture.Factory.Anonymous().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var client = fixture.Factory.Anonymous();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ForeignToken());

        var response = await client.GetAsync("/api/v1/appointments/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_returns_a_token_that_the_api_accepts()
    {
        var client = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());

        var response = await client.GetAsync("/api/v1/patients/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var email = ApiFlows.NewEmail();
        await fixture.Factory.AsNewPatientAsync(email);

        var response = await fixture.Factory.Anonymous()
            .PostAsJsonAsync("/api/v1/tokens", new { email, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // An address nobody holds must answer exactly as a wrong password does, or sign-in enumerates.
    [Fact]
    public async Task An_unknown_address_is_refused_the_same_way_as_a_wrong_password()
    {
        var email = ApiFlows.NewEmail();
        await fixture.Factory.AsNewPatientAsync(email);
        var client = fixture.Factory.Anonymous();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/tokens",
            new { email, password = "not-the-password" });

        var unknownAddress = await client.PostAsJsonAsync(
            "/api/v1/tokens",
            new { email = ApiFlows.NewEmail(), password = "not-the-password" });

        Assert.Equal(wrongPassword.StatusCode, unknownAddress.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownAddress.Content.ReadAsStringAsync());
    }

    // Registering must not answer whether the address was free, so it answers the same way twice.
    [Fact]
    public async Task Registering_an_address_that_is_already_taken_answers_as_the_first_did()
    {
        var email = ApiFlows.NewEmail();
        var client = fixture.Factory.Anonymous();

        var first = await Register(client, email);
        var second = await Register(client, email);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
    }

    // Both a route that answers and one that refuses, because the middleware runs ahead of either.
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1/appointments/mine")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var response = await fixture.Factory.Anonymous().GetAsync(path);
        var headers = response.Headers;

        Assert.Equal("nosniff", Assert.Single(headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(headers.GetValues("Referrer-Policy")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(headers.GetValues("Content-Security-Policy")));

        // The health endpoint adds a no-cache of its own, so no-store is the part that must be there.
        Assert.True(headers.CacheControl?.NoStore);
    }

    // A phone number is optional, so leaving it out is a registration and not an unhandled path.
    [Fact]
    public async Task Registering_without_a_phone_number_is_accepted()
    {
        var email = ApiFlows.NewEmail();

        var response = await fixture.Factory.Anonymous().PostAsJsonAsync("/api/v1/patients", new
        {
            fullName = "Ada Lovelace",
            email,
            password = ApiFlows.PatientPassword
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var token = await ApiFlows.TokenAsync(fixture.Factory.Anonymous(), email, ApiFlows.PatientPassword);
        Assert.NotEmpty(token);
    }

    [Fact]
    public async Task No_response_names_the_server_it_came_from()
    {
        var response = await fixture.Factory.Anonymous().GetAsync("/health");

        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    // The limit is the control; a host with it turned down is the only way to see the control work.
    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        await using var rationed = fixture.CreateHost(($"{RateLimitOptions.Section}:SensitivePerMinute", "2"));

        var client = rationed.Anonymous();
        var email = ApiFlows.NewEmail();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/tokens",
                new { email, password = "not-the-password" });

            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private static Task<HttpResponseMessage> Register(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/v1/patients", new
        {
            fullName = "Ada Lovelace",
            email,
            phone = "0000000000",
            password = ApiFlows.PatientPassword
        });

    private static string ForeignToken()
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("a-different-key-that-docbook-never-signed-with"));

        var token = new JwtSecurityToken(
            issuer: "DocBook",
            audience: "DocBookClients",
            claims: [new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "Staff")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
