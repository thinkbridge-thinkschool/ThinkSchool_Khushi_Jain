# Day 18 — Background jobs

A `BackgroundService` that drains a queue, and shuts down without tearing work in half.

## Run it

```bash
dotnet run --project day18_background_jobs
```

It enqueues 12 items of 400ms each, runs for a second, then asks the host to stop while work is still
in flight, so the shutdown path is the thing you actually watch.

## How it drains

The queue is a bounded `Channel<WorkItem>`. Bounded, not unbounded: an unbounded queue accepts work
faster than it can be done until the process runs out of memory, so the writer waits instead.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
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

    await DrainRemainingAsync();
}
```

## How it shuts down cleanly

The token means "stop taking new work", not "abandon what you are doing". That distinction is the
whole exercise.

`stoppingToken` reaches `ReadAsync` and the loop condition, and nowhere else. `RunAsync` takes
`CancellationToken.None`, so an item that has already started always finishes.

The loop is written this way on purpose. The obvious version is
`await foreach (var item in reader.ReadAllAsync(stoppingToken))`, and it does not work: `ReadAllAsync`
is `while (await WaitToReadAsync(ct)) while (TryRead(out item)) yield return item;`, and that inner
loop never re-checks the token. Once items are buffered it hands out every one of them after
cancellation, so the drain budget below is never reached. `ReadAsync` checks the token before it
returns anything, which is what makes the stop signal land between items.

`StopAsync` completes the channel writer first, so nothing new can be queued during shutdown, then
lets `base.StopAsync` cancel the token and wait for `ExecuteAsync` to return.

What is already queued is then drained on a budget. The budget is checked *before* each
`TryRead`, never after: reading an item and then deciding there is no time to run it would take that
item out of the queue and drop it, which is the "acked but never processed" bug that loses messages
silently. Checking first means every item is either run or still queued, and the count that gets
logged is the true one.

If the budget expires the remaining items stay in the queue and are logged by count — the host kills
the process at `ShutdownTimeout` regardless, so this reports the loss rather than pretending it did
not happen. On an in-memory queue that report is all you get; the same shape over a durable queue is
what makes the items redeliverable on the next start.

Three budgets have to stay in order, and `ExecuteAsync` throws at startup if the last two are not:

| Budget | Value | Why |
|---|---|---|
| One item | 400ms | Must be shorter than the drain budget or nothing drains |
| Drain | 2s | Bounds the backlog, and leaves room for the in-flight item |
| `ShutdownTimeout` | 5s | The host kills the process here regardless |

## What the run shows

```
11:19:31.724 QueueDrainer  Item 3 started.
11:19:31.914 demo          Requesting shutdown while work is still in flight.
11:19:31.918 Lifetime      Application is shutting down...
11:19:32.126 QueueDrainer  Item 3 finished.
11:19:32.126 QueueDrainer  Stop requested. No further items will be taken from the queue.
11:19:32.127 QueueDrainer  Item 4 started.
...
11:19:34.182 QueueDrainer  Item 8 finished.
11:19:34.183 QueueDrainer  Drain budget of 2000ms expired after 5 items. 4 items are still queued and will not run.
11:19:34.184 QueueDrainer  Drainer stopped. 4 items were never started.
11:19:34.184 QueueMetrics  Metrics hook: queue depth at shutdown is 4.
11:19:34.185 demo          Host stopped in 2270ms.
```

Item 3 was mid-flight when the stop arrived and still finished, 200ms after the shutdown signal.
Three processed before the signal, five drained after it, four left queued: twelve accounted for,
and the queue depth reported by the separate metrics hook agrees with the drainer's own count. The
host stopped in 2270ms, inside the five-second `ShutdownTimeout`.

## BackgroundService vs IHostedService

`BackgroundService` is a small base class over `IHostedService`. It exists because a raw
`IHostedService.StartAsync` must return quickly — the host awaits it before it finishes starting, so
a long-running loop in there stalls startup. `BackgroundService` gives you `ExecuteAsync`, keeps the
`Task`, and hands you the stopping token.

So the two are for different shapes of work, and this project runs one of each. `QueueMetrics` is an
`IHostedService`: it logs queue depth at start and at stop and returns immediately, which is what
start/stop hooks are for. `QueueDrainer` is a `BackgroundService` because it owns a loop.

## When Hangfire over a hosted service

When the schedule has to survive a restart — Hangfire keeps jobs, retries and cron state in a
database, so a queued job outlives the process; a hosted service holds everything in memory and
loses it, and every replica runs its own copy.

## What is not here

No Hangfire code. It needs a storage backend to do anything a hosted service cannot, and the
exercise asks for the contrast in a line rather than a second implementation.
