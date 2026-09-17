using DocBook.Notifications.Application;
using DocBook.Patients.Contracts;
using DocBook.Scheduling.Contracts;

namespace DocBook.Application.Tests;

public class AppointmentNotificationTests
{
    private static readonly Guid Appointment = Guid.NewGuid();
    private static readonly Guid PatientGuid = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly PatientContact Ada = new(PatientGuid, "Ada Lovelace", "ada@example.com");

    private readonly RecordingNotificationSender _sender = new();
    private readonly InMemoryHandledMessageLog _handled = new();
    private readonly FakePatientDirectory _patients = new(Ada);

    [Fact]
    public async Task A_booking_is_confirmed_to_the_patient()
    {
        await HandleBooking();

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(Ada, sent.Recipient);
        Assert.Equal("Your appointment is confirmed", sent.Subject);
        Assert.Equal(1, _handled.Count);
    }

    // Delivery is at least once, which is only harmless because of the handled-message log.
    [Fact]
    public async Task A_redelivered_booking_confirms_nothing_a_second_time()
    {
        await HandleBooking();
        await HandleBooking();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_cancellation_tells_the_patient_why()
    {
        await HandleCancellation();

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("Your appointment is cancelled", sent.Subject);
        Assert.Contains("going on holiday", sent.Body);
    }

    [Fact]
    public async Task A_redelivered_cancellation_tells_the_patient_nothing_a_second_time()
    {
        await HandleCancellation();
        await HandleCancellation();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_redelivered_reminder_tells_the_patient_nothing_a_second_time()
    {
        await HandleReminder();
        await HandleReminder();

        Assert.Single(_sender.Sent);
    }

    // The key is the appointment and the kind, not the appointment alone, or one message silences the rest.
    [Fact]
    public async Task One_appointment_is_confirmed_reminded_and_cancelled_in_turn()
    {
        await HandleBooking();
        await HandleReminder();
        await HandleCancellation();

        Assert.Equal(3, _sender.Sent.Count);
        Assert.Equal(3, _handled.Count);
    }

    // Recording it here would suppress the retry, and the patient would never be told at all.
    [Fact]
    public async Task A_patient_the_directory_cannot_find_is_not_recorded_as_handled()
    {
        var handler = new AppointmentBookedNotification(new FakePatientDirectory(), _sender, _handled);

        await handler.HandleAsync(
            new AppointmentBookedIntegrationEvent(Appointment, Guid.NewGuid(), PatientGuid, Start, Start.AddMinutes(30)),
            CancellationToken.None);

        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _handled.Count);
    }

    private Task HandleBooking() =>
        new AppointmentBookedNotification(_patients, _sender, _handled).HandleAsync(
            new AppointmentBookedIntegrationEvent(Appointment, Guid.NewGuid(), PatientGuid, Start, Start.AddMinutes(30)),
            CancellationToken.None);

    private Task HandleCancellation() =>
        new AppointmentCancelledNotification(_patients, _sender, _handled).HandleAsync(
            new AppointmentCancelledIntegrationEvent(Appointment, Guid.NewGuid(), PatientGuid, Start, "going on holiday"),
            CancellationToken.None);

    private Task HandleReminder() =>
        new AppointmentReminderNotification(_patients, _sender, _handled).HandleAsync(
            new AppointmentReminderDueIntegrationEvent(Appointment, Guid.NewGuid(), PatientGuid, Start),
            CancellationToken.None);
}
