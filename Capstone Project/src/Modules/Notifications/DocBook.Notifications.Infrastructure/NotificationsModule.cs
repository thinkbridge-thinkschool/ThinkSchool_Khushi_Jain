using DocBook.Notifications.Application;
using DocBook.Scheduling.Contracts;
using DocBook.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Notifications.Infrastructure;

public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddScoped<INotificationSender, LoggingNotificationSender>();

        services.AddScoped<IIntegrationEventHandler<AppointmentBookedIntegrationEvent>, AppointmentBookedNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentCancelledIntegrationEvent>, AppointmentCancelledNotification>();
        services.AddScoped<IIntegrationEventHandler<AppointmentReminderDueIntegrationEvent>, AppointmentReminderNotification>();

        return services;
    }
}
