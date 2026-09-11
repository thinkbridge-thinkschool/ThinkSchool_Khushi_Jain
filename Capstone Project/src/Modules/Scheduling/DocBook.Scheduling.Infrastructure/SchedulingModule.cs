using DocBook.Infrastructure;
using DocBook.Scheduling.Application;
using DocBook.Scheduling.Contracts;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Scheduling.Infrastructure;

// The module's one entry point into the host.
public static class SchedulingModule
{
    public static IServiceCollection AddScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SchedulingDbContext>(options =>
            options.UseSqlServer(configuration.ConnectionString(), sql =>
            {
                // Its own history table, or the two modules' migrations would fight over one.
                sql.MigrationsHistoryTable("__migrations", SchedulingDbContext.Schema);

                // Azure SQL drops connections as a matter of course, and so does a cold container.
                sql.EnableRetryOnFailure();
            }));

        services.Configure<ReminderOptions>(configuration.GetSection("Reminders"));
        services.Configure<OutboxOptions>(configuration.GetSection("Outbox"));

        services.AddScoped<OpenDoctorDayHandler>();
        services.AddScoped<BookAppointmentHandler>();
        services.AddScoped<CancelAppointmentHandler>();
        services.AddScoped<SweepRemindersHandler>();

        services.AddScoped<IDoctorDayScheduleRepository, DoctorDayScheduleRepository>();
        services.AddScoped<IIntegrationEventPublisher, OutboxIntegrationEventPublisher>();
        services.AddScoped<IOutboxStore, SchedulingOutboxStore>();
        services.AddScoped<IAuditTrail, SchedulingAuditTrail>();
        services.AddScoped<AppointmentQueries>();

        services.AddScoped<IDomainEventHandler<AppointmentBooked>, AppointmentBookedPublisher>();
        services.AddScoped<IDomainEventHandler<AppointmentCancelled>, AppointmentCancelledPublisher>();
        services.AddScoped<IDomainEventHandler<AppointmentReminderDue>, AppointmentReminderDuePublisher>();

        // Scheduling owns the outbox table, so it is the module that starts the shared dispatcher.
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<ReminderSweepService>();

        return services;
    }

    // Named here rather than found by reflection, so only these three can come back out of a row.
    public static void RegisterSchedulingEvents(this IntegrationEventTypeMap types)
    {
        types.Register<AppointmentBookedIntegrationEvent>();
        types.Register<AppointmentCancelledIntegrationEvent>();
        types.Register<AppointmentReminderDueIntegrationEvent>();
    }
}
