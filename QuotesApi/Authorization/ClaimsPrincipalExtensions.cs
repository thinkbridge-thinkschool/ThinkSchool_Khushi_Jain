using System.Security.Claims;

namespace QuotesApi.Authorization;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the "sub" claim value, which both InternalJwt and Entra tokens
    /// carry as the stable identifier of the calling identity.
    /// </summary>
    public static string? GetSubjectId(this ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
