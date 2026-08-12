using System.Security.Claims;
using FluentAssertions;
using QuotesApi.Authorization;

namespace QuotesApi.Tests;

public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetSubjectId_WithSubClaim_ReturnsItsValue()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "user-123")]);
        var principal = new ClaimsPrincipal(identity);

        var subjectId = principal.GetSubjectId();

        subjectId.Should().Be("user-123");
    }

    [Fact]
    public void GetSubjectId_WithOnlyNameIdentifierClaim_FallsBackToItsValue()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-456")]);
        var principal = new ClaimsPrincipal(identity);

        var subjectId = principal.GetSubjectId();

        subjectId.Should().Be("user-456");
    }

    [Fact]
    public void GetSubjectId_WithBothSubAndNameIdentifierClaims_PrefersSub()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "sub-value"),
            new Claim(ClaimTypes.NameIdentifier, "name-identifier-value")
        ]);
        var principal = new ClaimsPrincipal(identity);

        var subjectId = principal.GetSubjectId();

        subjectId.Should().Be("sub-value");
    }

    [Fact]
    public void GetSubjectId_WithNeitherClaim_ReturnsNull()
    {
        var identity = new ClaimsIdentity([new Claim("scope", "quotes.write")]);
        var principal = new ClaimsPrincipal(identity);

        var subjectId = principal.GetSubjectId();

        subjectId.Should().BeNull();
    }

    [Fact]
    public void GetSubjectId_WithUnauthenticatedEmptyPrincipal_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var subjectId = principal.GetSubjectId();

        subjectId.Should().BeNull();
    }
}
