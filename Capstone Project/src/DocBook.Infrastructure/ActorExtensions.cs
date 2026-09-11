using System.Security.Claims;
using DocBook.SharedKernel;

namespace DocBook.Infrastructure;

public static class ClaimNames
{
    public const string Subject = "sub";

    public const string Role = "role";
}

public static class Policies
{
    public const string Staff = "staff";
}

public static class RateLimits
{
    // For the routes an anonymous caller can reach, which are the ones worth grinding at.
    public const string Sensitive = "sensitive";
}

public static class ActorExtensions
{
    // The only place an identity is built, and it is built from the token rather than the body.
    public static Actor Require(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(ClaimNames.Subject)?.Value;

        if (!Guid.TryParse(subject, out var id))
        {
            throw new InvalidOperationException("An authenticated caller has no usable subject claim.");
        }

        var role = principal.IsInRole(nameof(ActorRole.Staff))
            ? ActorRole.Staff
            : ActorRole.Patient;

        return new Actor(id, role);
    }
}
