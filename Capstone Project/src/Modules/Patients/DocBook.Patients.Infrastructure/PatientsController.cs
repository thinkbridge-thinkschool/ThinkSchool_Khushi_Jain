using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DocBook.Infrastructure;
using DocBook.Patients.Application;
using DocBook.Patients.Domain;
using DocBook.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace DocBook.Patients.Infrastructure;

public sealed record RegisterPatientRequest(
    [property: Required]
    [property: StringLength(Patient.MaxNameLength, MinimumLength = 1)]
    string FullName,

    [property: Required]
    [property: EmailAddress]
    [property: StringLength(Patient.MaxEmailLength)]
    string Email,

    // Optional, and nullable so that omitting it is a registration rather than a server error.
    [property: StringLength(Patient.MaxPhoneLength)]
    string? Phone,

    [property: Required]
    [property: StringLength(128, MinimumLength = 12)]
    string Password);

public sealed record TokenRequest(
    [property: Required]
    [property: StringLength(Patient.MaxEmailLength)]
    string Email,

    [property: Required]
    [property: StringLength(128)]
    string Password);

public static class PatientsController
{
    public static void MapPatientEndpoints(this IEndpointRouteBuilder routes)
    {
        var anonymous = routes.MapGroup("/api/v1").RequireRateLimiting(RateLimits.Sensitive);

        // Always 202, so a taken address cannot be told from a free one by registering against it.
        anonymous.MapPost("/patients", async (
            RegisterPatientRequest request,
            RegisterPatientHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new RegisterPatient(request.FullName, request.Email, request.Phone ?? string.Empty, request.Password),
                cancellationToken);

            return Results.Accepted();
        })
        .WithSummary("Register as a patient.");

        anonymous.MapPost("/tokens", async (
            TokenRequest request,
            AuthenticatePatientHandler handler,
            TokenService tokens,
            IPasswordHasher passwords,
            IOptions<StaffOptions> staffOptions,
            CancellationToken cancellationToken) =>
        {
            var staff = staffOptions.Value;

            if (string.Equals(staff.Email, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return passwords.Verify(request.Password, staff.PasswordHash)
                    ? Issued(tokens, new Actor(staff.Id, ActorRole.Staff))
                    : Results.Unauthorized();
            }

            var patientId = await handler.HandleAsync(
                new AuthenticatePatient(request.Email, request.Password),
                cancellationToken);

            return patientId is { } id
                ? Issued(tokens, new Actor(id, ActorRole.Patient))
                : Results.Unauthorized();
        })
        .WithSummary("Exchange an email and password for an access token.");

        var authenticated = routes.MapGroup("/api/v1").RequireAuthorization();

        authenticated.MapGet("/patients/me", async (
            ClaimsPrincipal principal,
            IPatientRepository patients,
            CancellationToken cancellationToken) =>
        {
            var actor = principal.Require();
            var patient = await patients.FindAsync(new PatientId(actor.Id), cancellationToken);

            return patient is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    patientId = patient.Id.Value,
                    fullName = patient.FullName,
                    email = patient.Email,
                    phone = patient.Phone,
                    registeredAt = patient.RegisteredAt
                });
        })
        .WithSummary("Read my own patient record.");
    }

    private static IResult Issued(TokenService tokens, Actor actor) =>
        Results.Ok(new
        {
            access_token = tokens.Issue(actor),
            token_type = "Bearer",
            expires_in = tokens.LifetimeSeconds
        });
}
