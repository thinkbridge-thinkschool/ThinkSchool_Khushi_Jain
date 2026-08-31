using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddHostedService<QueueMetrics>();
builder.Services.AddHostedService<QueueDrainer>();

var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("demo");
var queue = host.Services.GetRequiredService<WorkQueue>();

await host.StartAsync();

for (var id = 1; id <= 12; id++)
{
    await queue.EnqueueAsync(new WorkItem(id, TimeSpan.FromMilliseconds(400)), CancellationToken.None);
}

log.LogInformation("Enqueued 12 items, each taking 400ms. Letting the drainer run for 1s.");
await Task.Delay(TimeSpan.FromSeconds(1));

log.LogInformation("Requesting shutdown while work is still in flight.");
var stopwatch = Stopwatch.StartNew();
await host.StopAsync();
log.LogInformation("Host stopped in {Elapsed}ms.", stopwatch.ElapsedMilliseconds);

public sealed record WorkItem(int Id, TimeSpan Duration);

public sealed class WorkQueue
{
    private readonly Channel<WorkItem> channel = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public int Depth => channel.Reader.Count;

    public ValueTask EnqueueAsync(WorkItem item, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(item, cancellationToken);

    public ValueTask<WorkItem> ReadAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAsync(cancellationToken);

    public bool TryRead(out WorkItem item) => channel.Reader.TryRead(out item!);

    public void StopAcceptingWork() => channel.Writer.TryComplete();
}

public sealed class QueueDrainer(
    WorkQueue queue,
    IOptions<HostOptions> hostOptions,
    ILogger<QueueDrainer> logger) : BackgroundService
{
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    private readonly TimeSpan shutdownTimeout = hostOptions.Value.ShutdownTimeout;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DrainBudget >= shutdownTimeout)
        {
            throw new InvalidOperationException(
                $"The drain budget ({DrainBudget}) must be shorter than the host's shutdown timeout ({shutdownTimeout}).");
        }

        logger.LogInformation("Drainer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            WorkItem item;

            try
            {
                item = await queue.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            await RunAsync(item);
        }

        logger.LogInformation("Stop requested. No further items will be taken from the queue.");

        await DrainRemainingAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.StopAcceptingWork();
        await base.StopAsync(cancellationToken);
        logger.LogInformation("Drainer stopped. {Depth} items were never started.", queue.Depth);
    }

    private async Task DrainRemainingAsync()
    {
        var deadline = Stopwatch.StartNew();
        var drained = 0;

        while (deadline.Elapsed < DrainBudget && queue.TryRead(out var item))
        {
            await RunAsync(item);
            drained++;
        }

        if (queue.Depth > 0)
        {
            logger.LogWarning(
                "Drain budget of {Budget}ms expired after {Drained} items. {Remaining} items are still queued and will not run.",
                DrainBudget.TotalMilliseconds,
                drained,
                queue.Depth);

            return;
        }

        logger.LogInformation("Queue fully drained. {Drained} items completed after the stop signal.", drained);
    }

    private async Task RunAsync(WorkItem item)
    {
        logger.LogInformation("Item {Id} started.", item.Id);
        await Task.Delay(item.Duration, CancellationToken.None);
        logger.LogInformation("Item {Id} finished.", item.Id);
    }
}

public sealed class QueueMetrics(WorkQueue queue, ILogger<QueueMetrics> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Metrics hook: queue depth at startup is {Depth}.", queue.Depth);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Metrics hook: queue depth at shutdown is {Depth}.", queue.Depth);

        return Task.CompletedTask;
    }
}
