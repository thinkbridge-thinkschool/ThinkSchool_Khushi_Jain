using DocBook.Notifications.Application;
using DocBook.Patients.Contracts;
using DocBook.SharedKernel;

namespace DocBook.Application.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class RecordingAuditTrail : IAuditTrail
{
    public List<(Actor Actor, string Action, Guid SubjectId)> Entries { get; } = [];

    public void Record(Actor actor, string action, Guid subjectId) => Entries.Add((actor, action, subjectId));
}

internal sealed class FakePatientDirectory(params PatientContact[] known) : IPatientDirectory
{
    private readonly Dictionary<Guid, PatientContact> _known = known.ToDictionary(contact => contact.PatientId);

    public Task<PatientContact?> FindAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult<PatientContact?>(_known.TryGetValue(patientId, out var contact) ? contact : null);
}

internal sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
{
    public List<IIntegrationEvent> Published { get; } = [];

    public void Publish(IIntegrationEvent integrationEvent) => Published.Add(integrationEvent);
}

internal sealed class RecordingNotificationSender : INotificationSender
{
    public List<(PatientContact Recipient, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(
        PatientContact recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        Sent.Add((recipient, subject, body));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryHandledMessageLog : IHandledMessageLog
{
    private readonly HashSet<(Guid AppointmentId, NotificationKind Kind)> _handled = [];

    public int Count => _handled.Count;

    public Task<bool> WasHandledAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(_handled.Contains((appointmentId, kind)));

    public Task RecordAsync(Guid appointmentId, NotificationKind kind, CancellationToken cancellationToken)
    {
        _handled.Add((appointmentId, kind));
        return Task.CompletedTask;
    }
}
