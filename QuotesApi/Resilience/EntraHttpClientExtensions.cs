using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace QuotesApi.Resilience;

public static class EntraHttpClientExtensions
{
    public const string ClientName = "EntraDiscovery";

    private const int RetryAttempts = 3;
    private const int BulkheadPermits = 10;
    private const int BreakerMinimumThroughput = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bulkhead -> total timeout -> retry -> circuit breaker -> attempt timeout,
    /// outermost first. The order is the design: a call the process has no
    /// capacity for is refused before it costs anything, the total timeout
    /// bounds the whole logical call including backoff, and the innermost
    /// timeout bounds one attempt rather than the entire retry loop.
    ///
    /// Nothing here swallows a failure. Once the pipeline is exhausted the
    /// original exception or response still reaches the caller.
    /// </summary>
    public static IHttpClientBuilder AddEntraResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("default", (pipeline, context) =>
        {
            var logger = context.ServiceProvider.GetRequiredService<ILogger<Program>>();

            // Outermost: shedding load is cheaper than queueing it, and a queue
            // of callers waiting on a dependency that is already saturated is
            // how one slow dependency takes the whole process with it.
            pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                Name = "bulkhead",
                DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                {
                    PermitLimit = BulkheadPermits,
                    QueueLimit = 0
                },
                OnRejected = args =>
                {
                    logger.LogWarning(
                        "Bulkhead full at {Permits} in flight for {ClientName}; the call was refused.",
                        BulkheadPermits,
                        ClientName);

                    return ValueTask.CompletedTask;
                }
            });

            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "total-timeout",
                Timeout = TotalTimeout
            });

            var retry = new HttpRetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = RetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = RetryDelay,

                // Jittered, so a burst of callers that failed together do not
                // all come back at the same instant and fail together again.
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retry {Attempt} of {Max} for {ClientName} after {DelayMs}ms due to {Reason}",
                        args.AttemptNumber + 1,
                        RetryAttempts,
                        ClientName,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message
                            ?? args.Outcome.Result?.StatusCode.ToString()
                            ?? "unknown failure");

                    return ValueTask.CompletedTask;
                }
            };

            // Replaying a request that may already have been applied is worse
            // than failing it, so POST, PATCH, PUT, DELETE and CONNECT opt out.
            retry.DisableForUnsafeHttpMethods();

            pipeline.AddRetry(retry);

            // Inside the retry: the breaker counts individual attempts, so a
            // dependency that is genuinely down trips it rather than being
            // retried three times per caller indefinitely.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = "circuit-breaker",
                FailureRatio = 0.5,
                MinimumThroughput = BreakerMinimumThroughput,
                SamplingDuration = SamplingDuration,
                BreakDuration = BreakDuration,
                OnOpened = args =>
                {
                    logger.LogError(
                        "Circuit OPEN for {ClientName} for {BreakMs}ms after repeated failures.",
                        ClientName,
                        args.BreakDuration.TotalMilliseconds);

                    return ValueTask.CompletedTask;
                },

                // A healed dependency does not announce itself; the breaker only
                // finds out by letting one probe through.
                OnHalfOpened = args =>
                {
                    logger.LogWarning(
                        "Circuit HALF-OPEN for {ClientName}; one probe is allowed through.",
                        ClientName);

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger.LogInformation(
                        "Circuit CLOSED for {ClientName}; normal traffic resumes.",
                        ClientName);

                    return ValueTask.CompletedTask;
                }
            });

            // Innermost, so it bounds a single attempt and the retry above still
            // gets its remaining attempts within the total budget.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "attempt-timeout",
                Timeout = AttemptTimeout,
                OnTimeout = args =>
                {
                    logger.LogWarning(
                        "Attempt to {ClientName} abandoned after {TimeoutMs}ms.",
                        ClientName,
                        args.Timeout.TotalMilliseconds);

                    return ValueTask.CompletedTask;
                }
            });
        });

        return builder;
    }
}
