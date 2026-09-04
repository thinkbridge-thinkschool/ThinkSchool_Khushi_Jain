using DocBook.Patients.Contracts;

namespace DocBook.Notifications.Application;

public interface INotificationSender
{
    Task SendAsync(PatientContact recipient, string subject, string body, CancellationToken cancellationToken);
}
