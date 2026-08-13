using System.IdentityModel.Tokens.Jwt;

namespace QuotesApi.Authorization;

public static class AuthenticationSchemeSelector
{
    public static string Select(string authorizationHeader)
    {
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "InternalJwt";
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var handler = new JwtSecurityTokenHandler();

        // CanReadToken is documented to fail closed -- it returns false rather
        // than throwing for malformed input -- so ReadJwtToken below is only
        // ever reached once the token is already known to be well-formed.
        if (handler.CanReadToken(token) &&
            handler.ReadJwtToken(token).Issuer
                .StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase))
        {
            return "Entra";
        }

        return "InternalJwt";
    }
}
