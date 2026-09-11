using System.Threading.RateLimiting;
using DocBook.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace DocBook.Api.Extensions;

public static class RateLimitingExtensions
{
    public static WebApplicationBuilder AddApiRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partitioned by caller, so one signed-in patient cannot spend everybody else's budget.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.Name ?? Address(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Registration and sign-in, where an attacker grinds rather than browses.
            limiter.AddPolicy(RateLimits.Sensitive, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    Address(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        return builder;
    }

    private static string Address(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
