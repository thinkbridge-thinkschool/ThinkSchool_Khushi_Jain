using QuotesApi.Models;
using QuotesApi.Time;

namespace QuotesApi.Services;

public enum RefreshTokenValidation
{
    Valid,
    Reused,
    Expired
}

/// <summary>
/// Decides whether a stored refresh token may be redeemed. Extracted from the
/// /api/auth/refresh endpoint so the reuse-detection and expiry rules are
/// unit-testable independent of EF Core and real wall-clock time.
/// </summary>
public sealed class RefreshTokenEvaluator(IClock clock)
{
    public RefreshTokenValidation Evaluate(RefreshToken storedToken)
    {
        if (storedToken.RevokedAt is not null)
        {
            return RefreshTokenValidation.Reused;
        }

        if (storedToken.ExpiresAt <= clock.UtcNow)
        {
            return RefreshTokenValidation.Expired;
        }

        return RefreshTokenValidation.Valid;
    }
}
