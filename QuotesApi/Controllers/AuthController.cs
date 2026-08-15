using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;
using QuotesApi.Time;

namespace QuotesApi.Controllers;

public static class AuthController
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (
            LoginRequest request,
            QuotesDbContext db,
            TokenService tokens,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == request.Email, cancellationToken);

            if (user is null ||
                !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            {
                return Results.Unauthorized();
            }

            var accessToken = tokens.CreateAccessToken(user);
            var refreshToken = tokens.GenerateRefreshToken();

            db.RefreshTokens.Add(new RefreshToken
            {
                TokenHash = tokens.HashRefreshToken(refreshToken),
                UserId = user.Id,
                ExpiresAt = clock.UtcNow.Add(RefreshTokenLifetime),

                // A fresh login starts a new family. Rotation keeps the family
                // id, which is what lets reuse detection revoke every
                // descendant of a leaked token without touching other sessions.
                FamilyId = Guid.NewGuid()
            });

            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                access_token = accessToken,
                refresh_token = refreshToken,
                expires_in = tokens.AccessTokenLifetimeSeconds
            });
        });

        group.MapPost("/refresh", async (
            RefreshTokenRequest request,
            QuotesDbContext db,
            TokenService tokens,
            RefreshTokenEvaluator refreshTokenEvaluator,
            IClock clock,
            ActivitySource activitySource,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var tokenHash = tokens.HashRefreshToken(request.RefreshToken);

            var storedToken = await db.RefreshTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

            if (storedToken is null)
            {
                return Results.Unauthorized();
            }

            var validation = refreshTokenEvaluator.Evaluate(storedToken);

            if (validation is RefreshTokenValidation.Reused)
            {
                // Revoking a whole token family is a multi-step,
                // security-sensitive operation. Automatic instrumentation would
                // only show it as disconnected EF query spans with nothing
                // tying them together as "one family got revoked".
                using var activity = activitySource.StartActivity("revoke-refresh-token-family");
                activity?.SetTag("user.id", storedToken.UserId);
                activity?.SetTag("family.id", storedToken.FamilyId);

                logger.LogWarning(
                    "Refresh token reuse detected for user {UserId} and family {FamilyId}",
                    storedToken.UserId,
                    storedToken.FamilyId);

                var familyTokens = await db.RefreshTokens
                    .Where(t => t.FamilyId == storedToken.FamilyId)
                    .ToListAsync(cancellationToken);

                activity?.SetTag("family.token_count", familyTokens.Count);

                foreach (var token in familyTokens)
                {
                    token.RevokedAt ??= clock.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);

                return Results.Unauthorized();
            }

            if (validation is RefreshTokenValidation.Expired)
            {
                return Results.Unauthorized();
            }

            var newRefreshToken = tokens.GenerateRefreshToken();
            var newRefreshTokenHash = tokens.HashRefreshToken(newRefreshToken);

            storedToken.RevokedAt = clock.UtcNow;
            storedToken.ReplacedByTokenHash = newRefreshTokenHash;

            db.RefreshTokens.Add(new RefreshToken
            {
                TokenHash = newRefreshTokenHash,
                UserId = storedToken.UserId,
                ExpiresAt = clock.UtcNow.Add(RefreshTokenLifetime),
                FamilyId = storedToken.FamilyId
            });

            var accessToken = tokens.CreateAccessToken(storedToken.User);

            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                access_token = accessToken,
                refresh_token = newRefreshToken,
                expires_in = tokens.AccessTokenLifetimeSeconds
            });
        });

        group.MapPost("/logout", async (
            RefreshTokenRequest request,
            QuotesDbContext db,
            TokenService tokens,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var tokenHash = tokens.HashRefreshToken(request.RefreshToken);

            var storedToken = await db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

            // Logout is idempotent: an unknown or already-revoked token is not
            // an error, and saying so would leak which tokens exist.
            if (storedToken is null)
            {
                return Results.NoContent();
            }

            storedToken.RevokedAt ??= clock.UtcNow;

            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });
    }
}
