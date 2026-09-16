using DocBook.Api.Extensions;
using DocBook.Infrastructure;
using DocBook.Notifications.Infrastructure;
using DocBook.Patients.Infrastructure;
using DocBook.Scheduling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Otherwise every response names the server and its version to anyone scanning.
    kestrel.AddServerHeader = false;

    // Nothing here is large, and the default thirty megabytes is a cheap way to hurt the process.
    kestrel.Limits.MaxRequestBodySize = 32 * 1024;
});

// First, so the signing key is in configuration before anything binds options out of it.
builder.AddKeyVaultConfiguration();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Turns the annotations on the request records into a filter that refuses before a handler runs.
builder.Services.AddValidation();

builder.AddApiAuthentication();
builder.AddApiRateLimiting();
builder.AddApiDocument();

var integrationEvents = new IntegrationEventTypeMap();
integrationEvents.RegisterSchedulingEvents();
builder.Services.AddSingleton(integrationEvents);

// Scoped, because the handlers it resolves are scoped and share the unit of work it saves into.
builder.Services.AddScoped<DomainEventDispatcher>();
builder.Services.AddScoped<TokenService>();

// The whole composition root: one deployable, three modules, each behind its own registration.
builder.Services.AddScheduling(builder.Configuration);
builder.Services.AddPatients(builder.Configuration);
builder.Services.AddNotifications(builder.Configuration);

var app = builder.Build();

app.UseSecurityHeaders();
app.UseApiProblems();

await app.MigrateAsync();

app.UseAuthentication();
app.UseAuthorization();

// After authentication, so a signed-in caller is limited as themselves rather than as their address.
app.UseRateLimiter();

app.MapHealthChecks("/health").AllowAnonymous();

// Not public: reading the contract hands over the shape of every route in one request.
app.MapOpenApi("/openapi/{documentName}.json").RequireAuthorization();

app.MapPatientEndpoints();
app.MapSchedulingEndpoints();

app.Run();
