using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;

namespace QuotesApi.Tests;

public class AuthenticationSchemeSelectorTests
{
    [Fact]
    public void SelectAuthenticationScheme_NoAuthorizationHeader_ReturnsInternalJwt()
    {
        var scheme = AuthenticationSchemeSelector.Select(string.Empty);

        scheme.Should().Be("InternalJwt");
    }

    [Fact]
    public void SelectAuthenticationScheme_BearerWithNonMicrosoftIssuer_ReturnsInternalJwt()
    {
        var token = CreateJwt(issuer: "QuotesApi");

        var scheme = AuthenticationSchemeSelector.Select($"Bearer {token}");

        scheme.Should().Be("InternalJwt");
    }

    [Fact]
    public void SelectAuthenticationScheme_BearerWithMicrosoftEntraIssuer_ReturnsEntra()
    {
        var token = CreateJwt(issuer: "https://login.microsoftonline.com/some-tenant-id/v2.0");

        var scheme = AuthenticationSchemeSelector.Select($"Bearer {token}");

        scheme.Should().Be("Entra");
    }

    [Fact]
    public void SelectAuthenticationScheme_MalformedBearerToken_FallsBackToInternalJwtWithoutThrowing()
    {
        var scheme = AuthenticationSchemeSelector.Select("Bearer not-a-jwt-at-all");

        scheme.Should().Be("InternalJwt");
    }

    private static string CreateJwt(string issuer)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: "any-audience",
            claims: [],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("unit-test-signing-key-not-used-elsewhere")),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
