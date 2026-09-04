using DocBook.Scheduling.Application;
using DocBook.Scheduling.Domain;
using DocBook.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Scheduling.Infrastructure;

// The module's one entry point into the host. Its adapters — EF Core mapping, repositories,
// endpoints and the reminder job — are registered here as they are built.
public static class SchedulingModule
{
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddScoped<OpenDoctorDayHandler>();
        services.AddScoped<BookAppointmentHandler>();
        services.AddScoped<CancelAppointmentHandler>();

        services.AddScoped<IDomainEventHandler<AppointmentBooked>, AppointmentBookedPublisher>();
        services.AddScoped<IDomainEventHandler<AppointmentCancelled>, AppointmentCancelledPublisher>();
        services.AddScoped<IDomainEventHandler<AppointmentReminderDue>, AppointmentReminderDuePublisher>();

        return services;
    }
}
