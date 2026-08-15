using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Models;
using Serilog;
using Serilog.Context;

namespace QuotesApi.Extensions;

public static class ApplicationPipelineExtensions
{
    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        // Every log line written while handling a request -- including the
        // request-summary line and anything the exception handler logs --
        // shares this TraceId, so one request's lines can be filtered together.
        // PushProperty must stay active for the whole downstream pipeline, so
        // next() is awaited inside the using block rather than returned from it.
        //
        // This reads Activity.Current.TraceId rather than TraceIdentifier so the
        // value matches the trace ID OpenTelemetry exports for the same request,
        // which is what lets a trace in Jaeger and its log lines in the console
        // be found by the same ID. TraceIdentifier is a fallback for requests
        // that somehow have no Activity.
        app.Use(async (context, next) =>
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

            using (LogContext.PushProperty("TraceId", traceId))
            {
                await next();
            }
        });

        app.UseSerilogRequestLogging();

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILogger<Program>>();

                logger.LogError(
                    context.Features.Get<IExceptionHandlerFeature>()?.Error,
                    "Unhandled API exception");

                await Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "An unexpected error occurred.")
                    .ExecuteAsync(context);
            });
        });

        return app;
    }

    /// <summary>
    /// Migrations run in every environment; the starter account does not.
    /// Outside Development, accounts are created through the normal auth flow.
    /// </summary>
    public static async Task MigrateAndSeedAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        await db.Database.MigrateAsync();

        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        var seedOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<SeedOptions>>()
            .Value;

        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();

        if (!seedOptions.IsConfigured)
        {
            logger.LogInformation(
                "Starter account not seeded: set Seed:AdminEmail and Seed:AdminPassword " +
                "in user secrets to create one.");

            return;
        }

        if (await db.Users.AnyAsync(u => u.Email == seedOptions.AdminEmail))
        {
            return;
        }

        db.Users.Add(new User
        {
            Email = seedOptions.AdminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(seedOptions.AdminPassword)
        });

        await db.SaveChangesAsync();

        logger.LogInformation("Starter account seeded.");
    }
}
