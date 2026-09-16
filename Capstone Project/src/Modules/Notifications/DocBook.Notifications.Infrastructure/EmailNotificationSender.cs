using Azure;
using Azure.Communication.Email;
using DocBook.Notifications.Application;
using DocBook.Patients.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocBook.Notifications.Infrastructure;

public sealed class EmailNotificationSender(
    EmailClient client,
    IOptions<EmailOptions> options,
    ILogger<EmailNotificationSender> logger) : INotificationSender
{
    public async Task SendAsync(PatientContact recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var message = new EmailMessage(
            options.Value.FromAddress,
            recipient.Email,
            new EmailContent(subject) { PlainText = body });

        // Started rather than Completed: the outbox already owns the retry, and waiting would hold the claim.
        await client.SendAsync(WaitUntil.Started, message, cancellationToken);

        // The recipient's address is the patient's, so only their id goes in the log line.
        logger.LogInformation("Notification {Subject} sent for patient {PatientId}.", subject, recipient.PatientId);
    }
}
