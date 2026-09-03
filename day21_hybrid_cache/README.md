# Day 21 — HybridCache and the stampede

A hot read — one quote by id — served three ways against the same data and the same load: straight
from the database, through a naive read-through cache, and through `HybridCache` with an in-memory
tier and a real Azure Cache for Redis behind it. One run measures all three and prints the numbers.

## Run it

```bash
az login
```

```bash
export REDIS_CONNECTION_STRING="$(bash day21_hybrid_cache/azure-setup.sh)"
```

The cache takes 15-20 minutes to create. It is billed by the hour, so delete the group afterwards:

```bash
az group delete --name rg-day21-cache --yes --no-wait
```

```bash
dotnet run -c Release --project day21_hybrid_cache
```

The default region is `eastus`. The round trip to Redis is part of what is being measured here, so
put the cache near the machine running the test: `AZURE_LOCATION=centralindia` in front of the
setup script picks a different one.

## The wiring

```csharp
var services = new ServiceCollection();

services.AddStackExchangeRedisCache(options => options.Configuration = redis);

services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(5),
    LocalCacheExpiration = TimeSpan.FromMinutes(5),
});
```

That is all of it. `HybridCache` picks up whatever `IDistributedCache` is registered and uses it as
its second tier, so registering the Redis cache is the whole of the L2 wiring. `Expiration` is how
long an entry lives in Redis; `LocalCacheExpiration` is how long the in-process copy lives.

The read:

```csharp
hybrid.GetOrCreateAsync(
    Key(phase, id),
    (store, id),
    static (state, token) => state.store.LoadAsync(state.id, token),
    cancellationToken: cancellationToken);
```

The factory is only called on a miss, and — this is the point of the exercise — only once per key
however many callers miss at the same moment.

The naive version is the same idea written by hand, and it is what most caching code looks like:

```csharp
var cached = await distributed.GetAsync(key, cancellationToken);

if (cached is not null)
{
    return JsonSerializer.Deserialize<Quote>(cached)!;
}

var quote = await store.LoadAsync(id, cancellationToken);

await distributed.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(quote), naiveEntry, cancellationToken);
```

Nothing between the get and the set coordinates callers, so on a cold key every caller in flight
loads the same row.

## How the numbers are measured

Database reads are counted, not estimated. An EF Core command interceptor increments a counter on
every command that reaches SQLite, and the same interceptor adds 25 ms to each one:

```csharp
Interlocked.Increment(ref reads);

await Task.Delay(latency, cancellationToken);
```

The delay is the one artificial thing in the setup. Local SQLite answers from the page cache in
microseconds, so without it a cache saves nothing measurable and a stampede is invisible. 25 ms is
roughly what a query to a database across a network costs.

Each load phase is 5,000 reads spread over 10 keys, 100 in flight at a time. Each stampede phase is
100 readers released at once against a single cold key. Every phase gets its own key namespace, and
so does every run, so no phase is ever warmed by another. The `HybridCache` load runs twice over the
same keys — once cold, then once warm, which is the state a cache spends nearly all its life in.

`no db` is the share of reads that did not reach the database. That is not quite a hit rate: in the
stampede phases the callers who waited on someone else's load were misses that got deduplicated,
not hits. The column is named for what it actually counts.

## Results

Before and after, on the same 5,000 reads over the same 10 keys:

| | no cache | HybridCache, warm |
|---|---|---|
| Database reads | 5,000 | 0 |
| Database reads/sec | 1,995 | 0 |
| p50 | 40.85 ms | below 0.01 ms |
| p99 | 76.48 ms | below 0.01 ms |
| Reads/sec | 1,995 | 404,577 |
| Whole phase | 2,507 ms | 12 ms |

The whole run:

```
phase                        reads  db reads    no db   reads/s  db reads/s   p50 ms   p99 ms
no cache                     5,000     5,000     0.0%     1,995       1,995    40.85    76.48
naive cache                  5,000       100    98.0%       365           7   257.89   585.61
HybridCache, cold            5,000        10    99.8%    14,038          28     0.00   313.17
HybridCache, warm            5,000         0   100.0%   404,577           0     0.00     0.00
naive, one cold key            100       100     0.0%       169         169   591.91   592.06
HybridCache, one cold key      100         1    99.0%       324           3   308.49   308.69
HybridCache, empty L1           10         0   100.0%        42           0   235.48   235.51
```

**The stampede, on its own.** 100 readers against one cold key: the naive cache made 100 database
reads, `HybridCache` made 1. Its p50 and p99 were 308.49 ms and 308.69 ms — 100 callers finishing a
fifth of a millisecond apart, because a single load released all of them at once. The naive run's
592 ms per caller is a Redis miss, the read, and a Redis write, done 100 times over.

**The stampede shows up in the load phase too.** The naive cache made 100 database reads to fill 10
keys. The extra 90 are the first wave of concurrent callers, all missing at the same instant.
`HybridCache` made exactly 10.

**Cold and warm are different questions.** The cold phase's p99 of 313 ms is the ~100 callers that
arrived on empty keys and waited for those 10 loads. In a 5,000-read phase that cold moment is 2% of
the reads, so it sits above p99 and makes the cache look worse than no cache on that one number.
Warm, the same load is 12 ms with no database reads at all. Stampede protection does not make the
first caller fast — it stops the other 99 from repeating its work.

**The naive cache was slower than no cache at all** — 365 reads/sec against 1,995, p99 586 ms
against 76 ms. It has no in-process tier, so every read is a round trip to Redis in eastus, and that
trip costs more than the read it replaced. That row is the argument for L1.

**Redis is genuinely serving reads.** A second `HybridCache` over the same cache, with an empty L1
and its connection already open, served all 10 keys with 0 database reads at 235 ms each. That
235 ms is one round trip to eastus, and it is what an L1 hit of roughly 2 µs is avoiding.

Two things about these numbers I would not read past. The database here is a local file and the
cache is a region away, which is backwards from a real deployment where both are remote and Redis is
the closer of the two — so the naive row is harsher here than it would be in production. And the
uncached p50 came out at 40.85 ms against a 25 ms injected delay, because 100 concurrent timers and
thread-pool scheduling put a floor under it. What distance does not change is the database read
count, which is what the exercise asks for: 5,000 uncached, 10 cold, 0 warm.

## What would break this

The in-memory tier is per process. Ten instances behind a load balancer each keep their own L1, so a
cold key can still produce ten database reads — one per instance — and L1 entries can be stale by up
to `LocalCacheExpiration` after another instance writes. `RemoveAsync` only reaches the local L1 and
Redis, not the L1 of the other nine.

Stampede protection is also per instance. It collapses concurrent callers inside one process, which
is where the fan-out is worst, but ten processes missing at once still send ten loads.

Nothing here invalidates on write, so a quote edited in the database keeps serving its old text
until the entry expires. Real use needs the write path to remove or overwrite the key, or tags and
`RemoveByTagAsync`.

A load that misses on many different keys at once gets no help at all from any of this. Stampede
protection dedupes by key, and a thousand distinct cold keys are a thousand real loads.
