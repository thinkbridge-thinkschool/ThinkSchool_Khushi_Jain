using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Models;

namespace QuotesApi.Services;

/// <summary>
/// Mints access tokens and the opaque refresh tokens that accompany them.
/// Refresh tokens are returned to the caller in the clear exactly once and
/// stored only as a SHA-256 hash, so a database leak does not yield usable
/// tokens.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> jwtOptions)
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public int AccessTokenLifetimeSeconds => _options.AccessTokenMinutes * 60;

    public string CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iss, _options.Issuer),
            new Claim(JwtRegisteredClaimNames.Aud, _options.Audience),
            new Claim("scope", "quotes.write")
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
