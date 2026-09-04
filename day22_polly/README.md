# Day 22 — Resilience with Polly

One outbound HTTP dependency wrapped in a Polly pipeline: a bulkhead, a timeout, retry with backoff,
and a circuit breaker. The dependency is a small service the program starts itself on a loopback port,
so I can make it fail, make it slow, and make it healthy again on cue.

## Run it

```bash
dotnet run -c Release --project day22_polly
```

About 17 seconds. Nothing to set up and nothing in Azure.

## The pipeline

```csharp
services.AddHttpClient(name, client => client.BaseAddress = baseAddress)
    .AddResilienceHandler(name, (pipeline, context) =>
    {
        pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
        {
            Name = "bulkhead",
            DefaultRateLimiterOptions = new ConcurrencyLimiterOptions { PermitLimit = 5, QueueLimit = 0 },
        });

        pipeline.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = "total-timeout",
            Timeout = TimeSpan.FromSeconds(30),
        });

        var retry = new HttpRetryStrategyOptions
        {
            Name = "retry",
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
            UseJitter = true,
        };

        retry.DisableForUnsafeHttpMethods();

        pipeline.AddRetry(retry);

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            Name = "circuit-breaker",
            FailureRatio = 0.5,
            MinimumThroughput = 8,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            StateProvider = state,
        });

        pipeline.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = "attempt-timeout",
            Timeout = TimeSpan.FromSeconds(2),
        });
    });
```

The callbacks that produce the log lines below are cut from this snippet.

Polly runs strategies outside-in, so the order is the design:

| position | strategy | why it sits there |
|---|---|---|
| outermost | bulkhead | refuses a call before it costs a connection or a retry slot |
| | total timeout | bounds the whole call, retries and backoff included |
| | retry | outside the breaker, so retries are subject to the circuit |
| | circuit breaker | counts what individual attempts did |
| innermost | attempt timeout | bounds one attempt, not the retry loop |

`DisableForUnsafeHttpMethods()` is the idempotency rule — it turns retry off for POST, PATCH, PUT,
DELETE and CONNECT. It returns nothing and edits the options in place. A failed request is not the
same as a request that never happened, so retrying anything with a side effect risks doing it twice.

## The run

Polly's own telemetry repeats every event with a full stack trace, so it is filtered off and these are
my callbacks. The breaker going closed → open → half-open → closed:

```
11:43:13.793 info: day22[0] breaker: dependency healthy: 5/5 ok, 5 request(s) reached the dependency, 7 ms, circuit Closed — ok
11:43:13.794 warn: day22:breaker[0] retry 1/3 in 150 ms after HTTP 503.
11:43:13.949 warn: day22:breaker[0] retry 2/3 in 168 ms after HTTP 503.
11:43:14.126 warn: day22:breaker[0] retry 3/3 in 423 ms after HTTP 503.
11:43:14.575 fail: day22:breaker[0] circuit OPEN for 5,000 ms after HTTP 503.
11:43:14.577 warn: day22:breaker[0] retry 1/3 in 179 ms after HTTP 503.
11:43:14.765 info: day22[0] breaker: dependency failing: 0/2 ok, 5 request(s) reached the dependency, 972 ms, circuit Open — BrokenCircuitException, HTTP 503
11:43:14.766 info: day22[0] breaker: open, calls shed: 0/5 ok, 0 request(s) reached the dependency, 1 ms, circuit Open — BrokenCircuitException
11:43:14.767 info: day22[0] breaker: healed, still open: 0/1 ok, 0 request(s) reached the dependency, 0 ms, circuit Open — BrokenCircuitException
11:43:14.767 info: day22[0] Waiting out the 5,000 ms break.
11:43:19.777 warn: day22:breaker[0] circuit HALF-OPEN, one probe allowed through.
11:43:19.789 info: day22:breaker[0] circuit CLOSED, normal traffic resumes.
11:43:19.789 info: day22[0] breaker: half-open probe: 1/1 ok, 1 request(s) reached the dependency, 19 ms, circuit Closed — ok
11:43:19.794 info: day22[0] breaker: recovered: 5/5 ok, 5 request(s) reached the dependency, 4 ms, circuit Closed — ok
```

Every phase, with a count of how many requests actually arrived at the dependency:

```
phase                             calls   ok  failed  reached dep       ms  outcome
retry: GET while failing              1    0       1            4    1,247  HTTP 503
retry: POST while failing             1    0       1            1        3  HTTP 503
timeout: GET while slow               1    0       1            4    9,041  TimeoutRejectedException
bulkhead: 20 GETs at once            20    5      15            5    1,019  ok, RateLimiterRejectedException
breaker: dependency healthy           5    5       0            5        7  ok
breaker: dependency failing           2    0       2            5      972  BrokenCircuitException, HTTP 503
breaker: open, calls shed             5    0       5            0        1  BrokenCircuitException
breaker: healed, still open           1    0       1            0        0  BrokenCircuitException
breaker: half-open probe              1    1       0            1       19  ok
breaker: recovered                    5    5       0            5        4  ok
```

Three things I would point at:

- While the circuit was open, six calls took 1 ms between them and none reached the dependency.
- `breaker: healed, still open` is the dependency answering 200s again while the circuit refuses to
  send anything. It only finds out by probing when the break expires.
- The second failing GET tipped the ratio, so the circuit opened at 14.575 — and the retry scheduled
  2 ms later ran into the open circuit and came back as `BrokenCircuitException` instead of another
  request. That is retry sitting outside the breaker.

## What would break this

The circuit and the bulkhead permits live in one process, so ten instances behind a load balancer
discover the same outage ten times over and the five permits are really fifty concurrent calls at the
dependency. Opening needs eight outcomes inside ten seconds, so an endpoint called twice a minute
never trips its breaker however broken the dependency is. And the half-open probe is a real request —
if the dependency is still down, that caller pays for the experiment.
