using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuotesApi.Resilience;

namespace QuotesApi.Tests;

/// <summary>
/// Forces transient failures through a fake handler (never touches the real
/// network) to prove the resilience pipeline retries, recovers, and logs
/// every attempt -- rather than silently swallowing them.
/// </summary>
public class EntraResilienceHandlerTests
{
    [Fact]
    public async Task Client_RecoversFromTransientFailures_AndLogsEachRetry()
    {
        var loggerProvider = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider));

        services.AddHttpClient(EntraHttpClientExtensions.ClientName)
            .AddEntraResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new FlakyHandler(failuresBeforeSuccess: 2));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(EntraHttpClientExtensions.ClientName);

        var response = await client.GetAsync("https://entra.test/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, loggerProvider.RetryWarnings.Count);
        Assert.All(loggerProvider.RetryWarnings, message => Assert.Contains("EntraDiscovery", message));
    }

    [Fact]
    public async Task Client_StillFailsWhenRetriesAreExhausted()
    {
        var loggerProvider = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider));

        services.AddHttpClient(EntraHttpClientExtensions.ClientName)
            .AddEntraResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new FlakyHandler(failuresBeforeSuccess: int.MaxValue));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(EntraHttpClientExtensions.ClientName);

        var response = await client.GetAsync("https://entra.test/.well-known/openid-configuration");

        // Persistent failure isn't hidden -- the caller still sees it after
        // the 3 configured retry attempts are exhausted.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, loggerProvider.RetryWarnings.Count);
    }

    private sealed class FlakyHandler(int failuresBeforeSuccess) : DelegatingHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _calls++;

            var statusCode = _calls <= failuresBeforeSuccess
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> RetryWarnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        // Microsoft.Extensions.Http.Resilience emits its own built-in
        // telemetry warnings for the same retry events, through a logger
        // category of its own. Filtering to only this app's Program
        // category keeps this test counting the code's own explicit
        // OnRetry logging, not Polly's separate internal telemetry log
        // lines for the identical event.
        private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && categoryName == typeof(Program).FullName)
                {
                    owner.RetryWarnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
