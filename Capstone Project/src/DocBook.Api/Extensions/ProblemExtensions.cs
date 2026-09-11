using DocBook.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Api.Extensions;

public static class ProblemExtensions
{
    // Rule messages are written for developers, so callers get these instead and never the detail.
    private static readonly Dictionary<string, (int Status, string Title)> ByCode = new()
    {
        ["reason_required"] = (StatusCodes.Status400BadRequest, "An appointment needs a reason."),
        ["reason_too_long"] = (StatusCodes.Status400BadRequest, "That reason is too long."),
        ["slot_in_past"] = (StatusCodes.Status400BadRequest, "An appointment cannot start in the past."),
        ["slot_wrong_day"] = (StatusCodes.Status400BadRequest, "That slot is not on the day requested."),
        ["slot_ends_before_it_starts"] = (StatusCodes.Status400BadRequest, "An appointment must end after it starts."),
        ["slot_length_invalid"] = (StatusCodes.Status400BadRequest, "That slot length is not valid."),
        ["day_closes_before_it_opens"] = (StatusCodes.Status400BadRequest, "A day must close after it opens."),

        // Closed and taken share this, so a refusal never says which of the two it was.
        ["slot_unavailable"] = (StatusCodes.Status409Conflict, "That slot is not available."),

        ["appointment_not_active"] = (StatusCodes.Status409Conflict, "That appointment is not active."),
        ["appointment_already_started"] = (StatusCodes.Status409Conflict, "That appointment has already started."),
        ["appointment_unknown"] = (StatusCodes.Status404NotFound, "No such appointment.")
    };

    public static WebApplication UseApiProblems(this WebApplication app)
    {
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;

            var (status, title) = Describe(error);

            if (status == StatusCodes.Status500InternalServerError)
            {
                logger.LogError(error, "Unhandled request failure.");
            }
            else
            {
                logger.LogInformation("Request refused as {Status}: {Detail}", status, error?.Message);
            }

            await Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
        }));

        return app;
    }

    private static (int Status, string Title) Describe(Exception? error) => error switch
    {
        DomainException domain when ByCode.TryGetValue(domain.Code, out var known) => known,
        DomainException => (StatusCodes.Status400BadRequest, "That request was refused."),

        // Two bookings raced for one slot and this one lost the concurrency check.
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "That slot is not available."),

        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
    };
}
