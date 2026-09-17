using System.Threading.RateLimiting;
using DocBook.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace DocBook.Api.Extensions;

// The defaults are the limits. Configuration exists so a test can exercise the limiter at all.
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int PerCallerPerMinute { get; init; } = 120;

    public int SensitivePerMinute { get; init; } = 5;
}

public static class RateLimitingExtensions
{
    public static WebApplicationBuilder AddApiRateLimiting(this WebApplicationBuilder builder)
    {
        var limits = builder.Configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>()
            ?? new RateLimitOptions();

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partitioned by caller, so one signed-in patient cannot spend everybody else's budget.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.Name ?? Address(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PerCallerPerMinute,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Registration and sign-in, where an attacker grinds rather than browses.
            limiter.AddPolicy(RateLimits.Sensitive, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    Address(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.SensitivePerMinute,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        return builder;
    }

    private static string Address(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
