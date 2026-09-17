using System.Text.Json;
using DocBook.Scheduling.Application;
using DocBook.Scheduling.Contracts;
using DocBook.Scheduling.Domain;

namespace DocBook.Application.Tests;

public class IntegrationEventPublisherTests
{
    private const string Reason = "suspected fracture";

    private static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly RecordingIntegrationEventPublisher _publisher = new();

    [Fact]
    public async Task A_booking_is_published_with_plain_ids()
    {
        var schedule = OpenDay();
        var appointment = Book(schedule);

        await new AppointmentBookedPublisher(_publisher)
            .HandleAsync((AppointmentBooked)schedule.DomainEvents[0], CancellationToken.None);

        var published = Assert.IsType<AppointmentBookedIntegrationEvent>(Assert.Single(_publisher.Published));
        Assert.Equal(appointment.Id.Value, published.AppointmentId);
        Assert.Equal(schedule.DoctorId.Value, published.DoctorId);
        Assert.Equal(Start, published.Start);
        Assert.Equal(Start.AddMinutes(30), published.End);
    }

    // The medical complaint is the most sensitive field DocBook holds, and it stays inside Scheduling.
    [Fact]
    public async Task No_published_booking_carries_the_reason_for_the_visit()
    {
        var schedule = OpenDay();
        Book(schedule);

        await new AppointmentBookedPublisher(_publisher)
            .HandleAsync((AppointmentBooked)schedule.DomainEvents[0], CancellationToken.None);

        Assert.DoesNotContain(Reason, Serialize(_publisher.Published[0]));
    }

    [Fact]
    public async Task A_cancellation_carries_why_it_was_cancelled_and_not_why_it_was_booked()
    {
        var schedule = OpenDay();
        var appointment = Book(schedule);
        schedule.ClearDomainEvents();
        schedule.Cancel(appointment.Id, "going on holiday", Now);

        await new AppointmentCancelledPublisher(_publisher)
            .HandleAsync((AppointmentCancelled)schedule.DomainEvents[0], CancellationToken.None);

        var payload = Serialize(_publisher.Published[0]);
        Assert.Contains("going on holiday", payload);
        Assert.DoesNotContain(Reason, payload);
    }

    [Fact]
    public async Task A_due_reminder_is_published_with_plain_ids()
    {
        var schedule = OpenDay();
        var appointment = Book(schedule);
        schedule.ClearDomainEvents();
        schedule.MarkRemindersDue(Now, TimeSpan.FromHours(24));

        await new AppointmentReminderDuePublisher(_publisher)
            .HandleAsync((AppointmentReminderDue)schedule.DomainEvents[0], CancellationToken.None);

        var published = Assert.IsType<AppointmentReminderDueIntegrationEvent>(Assert.Single(_publisher.Published));
        Assert.Equal(appointment.Id.Value, published.AppointmentId);
        Assert.DoesNotContain(Reason, Serialize(published));
    }

    // Serialized, because the outbox stores the event as JSON and that is what leaves the module.
    private static string Serialize(object integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType());

    private static DoctorDaySchedule OpenDay() =>
        DoctorDaySchedule.Open(
            new DoctorId(Guid.NewGuid()),
            new DateOnly(2026, 3, 2),
            new TimeOnly(9, 0),
            new TimeOnly(17, 0));

    private static Appointment Book(DoctorDaySchedule schedule) =>
        schedule.Book(
            new PatientId(Guid.NewGuid()),
            new TimeSlot(Start, Start.AddMinutes(30)),
            Reason,
            Now);
}
