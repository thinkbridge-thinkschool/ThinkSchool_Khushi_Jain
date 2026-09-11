using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DocBook.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocBook.Infrastructure;

public sealed class JwtOptions
{
    public const string MissingKeyMessage =
        "Jwt:SigningKey is required and must be at least 32 characters. Set it in user secrets.";

    public string Issuer { get; init; } = "DocBook";

    public string Audience { get; init; } = "DocBookClients";

    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    public bool HasSigningKey => SigningKey.Length >= 32;
}

// Access tokens only: nothing to revoke, and a stolen one expires within the quarter hour.
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public int LifetimeSeconds => _options.AccessTokenMinutes * 60;

    public string Issue(Actor actor)
    {
        var claims = new[]
        {
            new Claim(ClaimNames.Subject, actor.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new Claim(ClaimNames.Role, actor.Role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: clock.GetUtcNow().UtcDateTime.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
