using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace QuotesApi.Resilience;

public static class EntraHttpClientExtensions
{
    public const string ClientName = "EntraDiscovery";

    /// <summary>
    /// Retry (3 attempts, exponential backoff, jittered) -> circuit breaker
    /// (opens at 50% failures over a 30s window) -> a 10s timeout per attempt.
    /// Every retry is logged; nothing here swallows a failure -- once the
    /// pipeline is exhausted, the original exception/response still
    /// propagates to the caller.
    /// </summary>
    public static IHttpClientBuilder AddEntraResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("default", (pipeline, context) =>
        {
            var logger = context.ServiceProvider.GetRequiredService<ILogger<Program>>();

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retry {Attempt} for {ClientName} after {DelayMs}ms due to {Reason}",
                        args.AttemptNumber + 1,
                        ClientName,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message
                            ?? args.Outcome.Result?.StatusCode.ToString()
                            ?? "unknown failure");
                    return ValueTask.CompletedTask;
                }
            });

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                OnOpened = args =>
                {
                    logger.LogError(
                        "Circuit breaker opened for {ClientName} after repeated failures.",
                        ClientName);
                    return ValueTask.CompletedTask;
                }
            });

            pipeline.AddTimeout(TimeSpan.FromSeconds(10));
        });

        return builder;
    }
}
