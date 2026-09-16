using Azure.Communication.Email;
using DocBook.Infrastructure;
using DocBook.Notifications.Application;
using DocBook.Scheduling.Contracts;
using DocBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocBook.Notifications.Infrastructure;

public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseSqlServer(configuration.ConnectionString(), sql =>
            {
                // Its own history table, or the three modules' migrations would fight over one.
                sql.MigrationsHistoryTable("__migrations", NotificationsDbContext.Schema);

                // Azure SQL drops connections as a matter of course, and so does a cold container.
                sql.EnableRetryOnFailure();
            }));

        // Refused at startup rather than at the first booking: email on with no account sends nothing.
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.Section))
            .Validate(email => email.IsUsable, EmailOptions.MissingSettingsMessage)
            .ValidateOnStart();

        if (configuration.GetValue<bool>($"{EmailOptions.Section}:Enabled"))
        {
            services.AddSingleton(provider =>
                new EmailClient(provider.GetRequiredService<IOptions<EmailOptions>>().Value.ConnectionString));

            services.AddScoped<INotificationSender, EmailNotificationSender>();
        }
        else
        {
            services.AddScoped<INotificationSender, LoggingNotificationSender>();
        }

        services.AddScoped<IHandledMessageLog, HandledMessageLog>();

        services.AddScoped<IIntegrationEventHandler<AppointmentBookedIntegrationEvent>, AppointmentBookedNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentCancelledIntegrationEvent>, AppointmentCancelledNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentReminderDueIntegrationEvent>, AppointmentReminderNotification>();

        return services;
    }
}
