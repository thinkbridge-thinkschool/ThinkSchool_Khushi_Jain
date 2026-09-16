using DocBook.Infrastructure;
using DocBook.Notifications.Application;
using DocBook.Scheduling.Contracts;
using DocBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddScoped<INotificationSender, LoggingNotificationSender>();
        services.AddScoped<IHandledMessageLog, HandledMessageLog>();

        services.AddScoped<IIntegrationEventHandler<AppointmentBookedIntegrationEvent>, AppointmentBookedNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentCancelledIntegrationEvent>, AppointmentCancelledNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentReminderDueIntegrationEvent>, AppointmentReminderNotification>();

        return services;
    }
}
