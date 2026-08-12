using FluentAssertions;
using NSubstitute;
using QuotesApi.Models;
using QuotesApi.Services;
using QuotesApi.Time;

namespace QuotesApi.Tests;

public class RefreshTokenEvaluatorTests
{
    [Fact]
    public void Evaluate_TokenNotRevokedAndNotExpired_ReturnsValid()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = null,
            ExpiresAt = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero)
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Valid);
    }

    [Fact]
    public void Evaluate_RevokedToken_ReturnsReused()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero)
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Reused);
    }

    [Fact]
    public void Evaluate_ExpiredUnrevokedToken_ReturnsExpired()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = null,
            ExpiresAt = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero)
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Expired);
    }

    [Fact]
    public void Evaluate_ExpiresAtExactlyEqualsNow_ReturnsExpired()
    {
        // ExpiresAt <= now is treated as expired, so the exact boundary instant must expire too.
        var now = new DateTimeOffset(2026, 1, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = null,
            ExpiresAt = now
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Expired);
    }

    [Fact]
    public void Evaluate_RevokedAndExpiredToken_ReusePrecedesExpiry()
    {
        // Reuse detection must win when both conditions are true, matching the
        // production endpoint's check order (revocation is checked first).
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero)
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Reused);
    }

    [Fact]
    public void Evaluate_TokenOneSecondFromExpiry_ReturnsValid()
    {
        var expiresAt = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(expiresAt.AddSeconds(-1));
        var evaluator = new RefreshTokenEvaluator(clock);
        var storedToken = new RefreshToken
        {
            RevokedAt = null,
            ExpiresAt = expiresAt
        };

        var result = evaluator.Evaluate(storedToken);

        result.Should().Be(RefreshTokenValidation.Valid);
    }
}
