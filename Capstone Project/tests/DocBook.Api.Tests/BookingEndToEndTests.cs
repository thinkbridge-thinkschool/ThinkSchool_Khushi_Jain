using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using DocBook.Infrastructure;
using DocBook.Notifications.Application;
using DocBook.Patients.Contracts;
using DocBook.Scheduling.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocBook.Api.Tests;

// What every other test stops short of: a booking, the dispatcher's own pass, and a notification.
[Collection(nameof(ApiCollection))]
public class BookingEndToEndTests(DocBookApiFixture fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Guid _doctor = Guid.NewGuid();
    private readonly DateOnly _date = ApiFlows.Tomorrow();

    [Fact]
    public async Task A_booking_reaches_the_patient_once_however_often_it_is_delivered()
    {
        var sent = new SentNotifications();

        await using var host = fixture.CreateDispatchingHost(services =>
        {
            services.AddSingleton(sent);
            services.RemoveAll<INotificationSender>();
            services.AddScoped<INotificationSender, RecordingNotificationSender>();
        });

        var staff = await host.AsStaffAsync();
        await ApiFlows.OpenDayAsync(staff, _doctor, _date);

        var patient = await host.AsNewPatientAsync(ApiFlows.NewEmail());
        var patientId = await PatientIdAsync(patient);
        var slot = (await ApiFlows.FreeSlotsAsync(patient, _doctor, _date))[0];

        // The booking answers before anything is sent: the confirmation is a row, not a call.
        var appointmentId = await ApiFlows.BookedAppointmentAsync(patient, _doctor, slot);

        await Eventually(
            () => sent.To(patientId).Count == 1,
            "the dispatcher to send the confirmation");

        await Eventually(
            () => WasHandledAsync(host.Services, appointmentId),
            "the confirmation to be written to the handled-message log");

        await Eventually(
            async () => (await MessageAsync(host.Services, appointmentId)).ProcessedAt is not null,
            "the outbox row to be stamped processed");

        Assert.Equal("Your appointment is confirmed", Assert.Single(sent.To(patientId)));

        var delivered = await MessageAsync(host.Services, appointmentId);

        // What a redelivery looks like from the table: no lease, no stamp, pending all over again.
        await RedeliverAsync(host.Services, delivered.Id);

        await Eventually(
            async () =>
            {
                var again = await MessageAsync(host.Services, appointmentId);

                return again.Attempts > delivered.Attempts && again.ProcessedAt is not null;
            },
            "the message to be delivered a second time");

        Assert.Single(sent.To(patientId));
    }

    private static async Task<Guid> PatientIdAsync(HttpClient patient)
    {
        var me = await patient.GetFromJsonAsync<JsonElement>("/api/v1/patients/me");

        return me.GetProperty("patientId").GetGuid();
    }

    private static async Task<bool> WasHandledAsync(IServiceProvider services, Guid appointmentId)
    {
        using var scope = services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IHandledMessageLog>()
            .WasHandledAsync(appointmentId, NotificationKind.Confirmation, CancellationToken.None);
    }

    // Found by its payload, because the outbox row is the only place the booking and the send meet.
    private static async Task<OutboxMessage> MessageAsync(IServiceProvider services, Guid appointmentId)
    {
        using var scope = services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<SchedulingDbContext>()
            .Outbox
            .AsNoTracking()
            .SingleAsync(message => message.Payload.Contains(appointmentId.ToString()));
    }

    private static async Task RedeliverAsync(IServiceProvider services, Guid messageId)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        var message = await context.Outbox.SingleAsync(candidate => candidate.Id == messageId);

        message.ProcessedAt = null;
        message.ClaimedUntil = null;
        message.ClaimedBy = null;

        await context.SaveChangesAsync();
    }

    private static Task Eventually(Func<bool> condition, string what) =>
        Eventually(() => Task.FromResult(condition()), what);

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Waited {Patience.TotalSeconds:0} seconds for {what}.");
    }
}

// Shared across the dispatcher's own scopes, so a test can see what a background pass sent.
public sealed class SentNotifications
{
    private readonly ConcurrentBag<(Guid PatientId, string Subject)> _sent = [];

    public void Add(Guid patientId, string subject) => _sent.Add((patientId, subject));

    public IReadOnlyList<string> To(Guid patientId) =>
        _sent.Where(entry => entry.PatientId == patientId)
            .Select(entry => entry.Subject)
            .ToList();
}

internal sealed class RecordingNotificationSender(SentNotifications sent) : INotificationSender
{
    public Task SendAsync(
        PatientContact recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        sent.Add(recipient.PatientId, subject);

        return Task.CompletedTask;
    }
}
