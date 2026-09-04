using DocBook.Notifications.Infrastructure;
using DocBook.Patients.Infrastructure;
using DocBook.Scheduling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

// The whole composition root: one deployable, three modules, each behind its own registration.
builder.Services.AddScheduling();
builder.Services.AddPatients();
builder.Services.AddNotifications();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
