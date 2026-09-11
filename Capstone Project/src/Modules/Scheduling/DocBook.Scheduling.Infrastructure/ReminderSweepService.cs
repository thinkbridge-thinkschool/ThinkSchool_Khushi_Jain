using DocBook.Scheduling.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocBook.Scheduling.Infrastructure;

public sealed class ReminderOptions
{
    public TimeSpan LeadTime { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    public int PageSize { get; init; } = 50;
}

// Stamping is what makes a reminder fire once, so running this often costs nothing.
public sealed class ReminderSweepService(
    IServiceScopeFactory scopes,
    IOptions<ReminderOptions> options,
    ILogger<ReminderSweepService> logger) : BackgroundService
{
    private readonly ReminderOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();

                var swept = await scope.ServiceProvider
                    .GetRequiredService<SweepRemindersHandler>()
                    .HandleAsync(_options.LeadTime, _options.PageSize, stoppingToken);

                logger.LogInformation("Reminder sweep covered {ScheduleCount} schedules.", swept);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The reminder sweep failed a pass.");
            }

            try
            {
                await Task.Delay(_options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
