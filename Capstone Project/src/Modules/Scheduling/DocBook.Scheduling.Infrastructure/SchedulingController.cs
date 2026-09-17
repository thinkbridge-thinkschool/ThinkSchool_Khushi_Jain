using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DocBook.Infrastructure;
using DocBook.Scheduling.Application;
using DocBook.Scheduling.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DocBook.Scheduling.Infrastructure;

public sealed record OpenDayRequest(
    [property: Required] DateOnly Date,
    [property: Required] TimeOnly OpensAt,
    [property: Required] TimeOnly ClosesAt);

public sealed record BookAppointmentRequest(
    [property: Required] Guid DoctorId,
    [property: Required] DateTimeOffset Start,
    [property: Required] DateTimeOffset End,

    [property: Required]
    [property: StringLength(Appointment.MaxReasonLength, MinimumLength = 1)]
    string Reason,

    // Staff booking for someone else. Left out, it means the caller.
    Guid? PatientId);

public sealed record CancelAppointmentRequest(
    [property: Required]
    [property: StringLength(Appointment.MaxReasonLength, MinimumLength = 1)]
    string Reason);

public static class SchedulingController
{
    private const int MaxPageSize = 50;
    private const int MinSlotMinutes = 5;
    private const int MaxSlotMinutes = 240;

    public static void MapSchedulingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1").RequireAuthorization();

        group.MapPost("/doctors/{doctorId:guid}/days", async (
            Guid doctorId,
            OpenDayRequest request,
            ClaimsPrincipal principal,
            OpenDoctorDayHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new OpenDoctorDay(doctorId, request.Date, request.OpensAt, request.ClosesAt),
                principal.Require(),
                cancellationToken);

            return result.Outcome switch
            {
                OpenDayOutcome.Opened => Results.Created(
                    $"/api/v1/doctors/{doctorId}/days/{request.Date:O}/free-slots",
                    new { scheduleId = result.ScheduleId }),
                OpenDayOutcome.AlreadyOpen => Results.Conflict(),
                _ => Results.Forbid()
            };
        })
        .RequireAuthorization(Policies.Staff)
        .WithSummary("Open a doctor's day for booking.");

        // Authenticated, because the complement of the free slots is the doctor's booked day.
        group.MapGet("/doctors/{doctorId:guid}/days/{date}/free-slots", async (
            Guid doctorId,
            DateOnly date,
            int? minutes,
            IDoctorDayScheduleRepository schedules,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var length = minutes.GetValueOrDefault(30);

            if (length is < MinSlotMinutes or > MaxSlotMinutes)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"Slot length must be between {MinSlotMinutes} and {MaxSlotMinutes} minutes."]
                });
            }

            var schedule = await schedules.FindForReadingAsync(new DoctorId(doctorId), date, cancellationToken);

            if (schedule is null)
            {
                return Results.NotFound();
            }

            var free = schedule
                .FreeSlots(TimeSpan.FromMinutes(length), clock.GetUtcNow())
                .Select(slot => new { start = slot.Start, end = slot.End });

            return Results.Ok(new { doctorId, date, slots = free });
        })
        .WithSummary("List the slots still open on a doctor's day.");

        group.MapPost("/appointments", async (
            BookAppointmentRequest request,
            ClaimsPrincipal principal,
            BookAppointmentHandler handler,
            CancellationToken cancellationToken) =>
        {
            var actor = principal.Require();

            var result = await handler.HandleAsync(
                new BookAppointment(
                    request.DoctorId,
                    request.PatientId ?? actor.Id,
                    request.Start,
                    request.End,
                    request.Reason),
                actor,
                cancellationToken);

            return result.Outcome switch
            {
                BookingOutcome.Booked => Results.Created(
                    $"/api/v1/appointments/{result.AppointmentId}",
                    new { appointmentId = result.AppointmentId }),

                // One answer whether the day was never opened or the patient does not exist.
                BookingOutcome.Unavailable => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "That appointment is not available."),

                _ => Results.Forbid()
            };
        })
        .WithSummary("Book an appointment.");

        group.MapPost("/appointments/{id:guid}/cancellation", async (
            Guid id,
            CancelAppointmentRequest request,
            ClaimsPrincipal principal,
            CancelAppointmentHandler handler,
            CancellationToken cancellationToken) =>
        {
            var outcome = await handler.HandleAsync(
                new CancelAppointment(id, request.Reason),
                principal.Require(),
                cancellationToken);

            // Someone else's appointment answers as one that does not exist, so ids cannot be tested.
            return outcome is CancellationOutcome.Cancelled
                ? Results.NoContent()
                : Results.NotFound();
        })
        .WithSummary("Cancel an appointment.");

        group.MapGet("/appointments/mine", async (
            int? page,
            int? size,
            ClaimsPrincipal principal,
            AppointmentQueries queries,
            CancellationToken cancellationToken) =>
        {
            var currentPage = page.GetValueOrDefault(1);
            var pageSize = size.GetValueOrDefault(20);

            if (currentPage < 1 || pageSize is < 1 or > MaxPageSize)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["page/size"] = [$"Page must be at least 1 and size between 1 and {MaxPageSize}."]
                });
            }

            var actor = principal.Require();

            var appointments = await queries.ForPatientAsync(
                actor.Id,
                (currentPage - 1) * pageSize,
                pageSize,
                cancellationToken);

            return Results.Ok(new { page = currentPage, size = pageSize, items = appointments });
        })
        .WithSummary("List my upcoming appointments.");
    }
}
