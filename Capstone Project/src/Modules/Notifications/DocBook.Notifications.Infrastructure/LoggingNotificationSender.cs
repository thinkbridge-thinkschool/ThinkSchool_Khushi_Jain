using DocBook.Notifications.Application;
using DocBook.Patients.Contracts;
using Microsoft.Extensions.Logging;

namespace DocBook.Notifications.Infrastructure;

// Stands in until an email or SMS adapter is built. Contact details stay out of the log line.
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(PatientContact recipient, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("Notification {Subject} queued for patient {PatientId}.", subject, recipient.PatientId);
        return Task.CompletedTask;
    }
}
