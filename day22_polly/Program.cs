using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

const int RetryAttempts = 3;
const int BreakerMinimumThroughput = 8;
const int BulkheadPermits = 5;
const int BulkheadBurst = 20;
const int SlowResponseMs = 4_000;
const int BusyResponseMs = 1_000;

var attemptTimeout = TimeSpan.FromSeconds(2);
var totalTimeout = TimeSpan.FromSeconds(30);
var retryDelay = TimeSpan.FromMilliseconds(200);
var samplingDuration = TimeSpan.FromSeconds(10);
var breakDuration = TimeSpan.FromSeconds(5);

var dependency = new DependencyControl();
var host = WebApplication.CreateSlimBuilder();

host.Logging.ClearProviders();
host.WebHost.UseUrls("http://127.0.0.1:0");

await using var quotesService = host.Build();

quotesService.MapGet("/quotes/{id:int}", (int id) => dependency.HandleAsync(id));
quotesService.MapPost("/quotes", () => dependency.HandleAsync(0));

await quotesService.StartAsync();

var baseAddress = new Uri(quotesService.Urls.First());
var breakers = new Dictionary<string, CircuitBreakerStateProvider>();
var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });

    // One line per attempt per client is noise next to the strategy callbacks below.
    logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

    // Polly's own telemetry repeats every event with a full stack trace; the callbacks below say the same thing.
    logging.AddFilter("Polly", LogLevel.None);
});

// One pipeline shape, four instances: a breaker is per pipeline, so each demo starts from a closed circuit.
foreach (var name in new[] { "retry", "timeout", "bulkhead", "breaker" })
{
    AddQuotesClient(name);
}

await using var provider = services.BuildServiceProvider();

var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("day22");
var clients = provider.GetRequiredService<IHttpClientFactory>();
var breaker = breakers["breaker"];
var results = new List<PhaseResult>();

log.LogInformation("Dependency listening on {BaseAddress}.", baseAddress);

log.LogInformation("--- retry: backoff on a failing GET, and no retry at all on a POST ---");

dependency.Healthy = false;

results.Add(await RunAsync("retry: GET while failing", "retry", HttpMethod.Get, 1, concurrent: false));
results.Add(await RunAsync("retry: POST while failing", "retry", HttpMethod.Post, 1, concurrent: false));

log.LogInformation("--- timeout: a dependency that answers slower than the attempt budget ---");

dependency.Healthy = true;
dependency.DelayMs = SlowResponseMs;

results.Add(await RunAsync("timeout: GET while slow", "timeout", HttpMethod.Get, 1, concurrent: false));

log.LogInformation("--- bulkhead: {Burst} callers against {Permits} permits ---", BulkheadBurst, BulkheadPermits);

dependency.DelayMs = BusyResponseMs;

results.Add(await RunAsync($"bulkhead: {BulkheadBurst} GETs at once", "bulkhead", HttpMethod.Get, BulkheadBurst, concurrent: true));

log.LogInformation("--- circuit breaker: closed, open, half-open, closed ---");

dependency.DelayMs = 0;
dependency.Healthy = true;

results.Add(await RunAsync("breaker: dependency healthy", "breaker", HttpMethod.Get, 5, concurrent: false));

dependency.Healthy = false;

results.Add(await RunAsync(
    "breaker: dependency failing",
    "breaker",
    HttpMethod.Get,
    5,
    concurrent: false,
    stopEarly: () => breaker.CircuitState != CircuitState.Closed));

results.Add(await RunAsync("breaker: open, calls shed", "breaker", HttpMethod.Get, 5, concurrent: false));

// The dependency is well again, but nothing has told the breaker that; it only finds out by probing.
dependency.Healthy = true;

results.Add(await RunAsync("breaker: healed, still open", "breaker", HttpMethod.Get, 1, concurrent: false));

log.LogInformation("Waiting out the {BreakMs:N0} ms break.", breakDuration.TotalMilliseconds);

await Task.Delay(breakDuration);

results.Add(await RunAsync("breaker: half-open probe", "breaker", HttpMethod.Get, 1, concurrent: false));
results.Add(await RunAsync("breaker: recovered", "breaker", HttpMethod.Get, 5, concurrent: false));

Console.WriteLine();
Console.WriteLine($"{"phase",-32}{"calls",7}{"ok",5}{"failed",8}{"reached dep",13}{"ms",9}  outcome");

foreach (var result in results)
{
    Console.WriteLine(
        $"{result.Label,-32}{result.Calls,7:N0}{result.Ok,5:N0}{result.Failed,8:N0}" +
        $"{result.Reached,13:N0}{result.ElapsedMs,9:N0}  {result.Outcome}");
}

void AddQuotesClient(string name)
{
    var state = new CircuitBreakerStateProvider();

    breakers[name] = state;

    services.AddHttpClient(name, client => client.BaseAddress = baseAddress)
        .AddResilienceHandler(name, (pipeline, context) =>
        {
            // Not "polly:..." — the filter above matches on category prefix and would silence these too.
            var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger($"day22:{name}");

            // Outermost, so a call the process has no capacity for is refused before it costs anything.
            pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                Name = "bulkhead",
                DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                {
                    PermitLimit = BulkheadPermits,
                    QueueLimit = 0,
                },
                OnRejected = args =>
                {
                    logger.LogWarning("bulkhead full at {Permits} in flight, call refused.", BulkheadPermits);

                    return ValueTask.CompletedTask;
                },
            });

            // The budget for the whole logical call, retries and their backoff included.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "total-timeout",
                Timeout = totalTimeout,
            });

            var retry = new HttpRetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = RetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = retryDelay,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "retry {Attempt}/{Max} in {DelayMs:N0} ms after {Reason}.",
                        args.AttemptNumber + 1,
                        RetryAttempts,
                        args.RetryDelay.TotalMilliseconds,
                        Failure.Describe(args.Outcome));

                    return ValueTask.CompletedTask;
                },
            };

            // Retry only what is safe to send twice: this opts POST, PATCH, PUT, DELETE and CONNECT out.
            retry.DisableForUnsafeHttpMethods();

            pipeline.AddRetry(retry);

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = "circuit-breaker",
                FailureRatio = 0.5,
                MinimumThroughput = BreakerMinimumThroughput,
                SamplingDuration = samplingDuration,
                BreakDuration = breakDuration,
                StateProvider = state,
                OnOpened = args =>
                {
                    logger.LogError(
                        "circuit OPEN for {BreakMs:N0} ms after {Reason}.",
                        args.BreakDuration.TotalMilliseconds,
                        Failure.Describe(args.Outcome));

                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger.LogWarning("circuit HALF-OPEN, one probe allowed through.");

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("circuit CLOSED, normal traffic resumes.");

                    return ValueTask.CompletedTask;
                },
            });

            // Innermost, so it bounds a single attempt rather than the whole retry loop.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "attempt-timeout",
                Timeout = attemptTimeout,
                OnTimeout = args =>
                {
                    logger.LogWarning("attempt abandoned after {TimeoutMs:N0} ms.", args.Timeout.TotalMilliseconds);

                    return ValueTask.CompletedTask;
                },
            });
        });
}

async Task<string> CallAsync(string clientName, HttpMethod method, string path)
{
    var client = clients.CreateClient(clientName);

    using var request = new HttpRequestMessage(method, path);

    if (method == HttpMethod.Post)
    {
        request.Content = new StringContent("""{"author":"Seneca","text":"Every new beginning."}""", Encoding.UTF8, "application/json");
    }

    try
    {
        using var response = await client.SendAsync(request);

        return response.IsSuccessStatusCode ? "ok" : Failure.Describe(response);
    }
    catch (Exception exception)
    {
        return Failure.Describe(exception);
    }
}

async Task<PhaseResult> RunAsync(
    string label,
    string clientName,
    HttpMethod method,
    int calls,
    bool concurrent,
    Func<bool>? stopEarly = null)
{
    var path = method == HttpMethod.Post ? "/quotes" : "/quotes/1";
    var reachedBefore = dependency.Requests;
    var clock = Stopwatch.StartNew();
    string[] outcomes;

    if (concurrent)
    {
        outcomes = await Task.WhenAll(Enumerable.Range(0, calls).Select(_ => CallAsync(clientName, method, path)));
    }
    else
    {
        var sequential = new List<string>(calls);

        for (var call = 0; call < calls; call++)
        {
            sequential.Add(await CallAsync(clientName, method, path));

            if (stopEarly?.Invoke() == true)
            {
                break;
            }
        }

        outcomes = [.. sequential];
    }

    clock.Stop();

    var result = new PhaseResult(
        label,
        outcomes.Length,
        outcomes.Count(outcome => outcome == "ok"),
        outcomes.Count(outcome => outcome != "ok"),
        dependency.Requests - reachedBefore,
        clock.Elapsed.TotalMilliseconds,
        string.Join(", ", outcomes.Distinct().Order()));

    log.LogInformation(
        "{Label}: {Ok}/{Calls} ok, {Reached} request(s) reached the dependency, {Elapsed:N0} ms, circuit {State} — {Outcome}",
        result.Label,
        result.Ok,
        result.Calls,
        result.Reached,
        result.ElapsedMs,
        breakers[clientName].CircuitState,
        result.Outcome);

    return result;
}

public sealed record PhaseResult(
    string Label,
    int Calls,
    int Ok,
    int Failed,
    int Reached,
    double ElapsedMs,
    string Outcome);

/// <summary>The outbound dependency, with a health switch and a request counter the demo drives.</summary>
public sealed class DependencyControl
{
    private volatile bool healthy = true;
    private volatile int delayMs;
    private int requests;

    public bool Healthy { get => healthy; set => healthy = value; }

    public int DelayMs { get => delayMs; set => delayMs = value; }

    /// <summary>Requests that actually arrived, so a shed call can be told apart from a served one.</summary>
    public int Requests => Volatile.Read(ref requests);

    public async Task<IResult> HandleAsync(int id)
    {
        Interlocked.Increment(ref requests);

        var delay = delayMs;

        if (delay > 0)
        {
            await Task.Delay(delay);
        }

        return healthy
            ? Results.Text($$"""{"id":{{id}},"author":"Seneca","text":"Luck is preparation meeting opportunity."}""", "application/json")
            : Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
    }
}

public static class Failure
{
    public static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is { } exception ? Describe(exception) : Describe(outcome.Result);

    public static string Describe(HttpResponseMessage? response) =>
        response is null ? "no response" : $"HTTP {(int)response.StatusCode}";

    // HttpClient can wrap a rejection, so report the innermost exception that names a strategy.
    public static string Describe(Exception exception) => exception switch
    {
        BrokenCircuitException => nameof(BrokenCircuitException),
        RateLimiterRejectedException => nameof(RateLimiterRejectedException),
        TimeoutRejectedException => nameof(TimeoutRejectedException),
        { InnerException: { } inner } => Describe(inner),
        _ => exception.GetType().Name,
    };
}
