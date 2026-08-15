using System.Text;

namespace QuotesApi.Models;

public sealed class JwtOptions
{
    /// <summary>
    /// HS256 requires a key of at least 256 bits; anything shorter is rejected
    /// outright by the signing credentials rather than silently weakening the
    /// signature.
    /// </summary>
    public const int MinimumKeyBytes = 32;

    public const string MissingKeyMessage =
        "Jwt:SigningKey is not configured, or is shorter than 256 bits. Secrets do not " +
        "belong in appsettings.json. Supply it with: " +
        "dotnet user-secrets set \"Jwt:SigningKey\" \"<value>\" --project QuotesApi";

    public string SigningKey { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    public bool HasSigningKey => Encoding.UTF8.GetByteCount(SigningKey) >= MinimumKeyBytes;
}
