using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

const int Requests = 5_000;
const int Keys = 10;
const int Concurrency = 100;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
}));

var log = loggerFactory.CreateLogger("day21");
var redis = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");

if (string.IsNullOrWhiteSpace(redis))
{
    log.LogError("Set REDIS_CONNECTION_STRING. See day21_hybrid_cache/README.md.");

    return 1;
}

var path = Path.Combine(AppContext.BaseDirectory, "day21-quotes.db");
var connectionString = $"Data Source={path}";
var interceptor = new DbReadInterceptor(TimeSpan.FromMilliseconds(25));
var store = new QuoteStore(connectionString, interceptor);

// Every phase shares one Redis, so each phase and each run gets its own key namespace.
var run = Guid.NewGuid().ToString("N")[..8];

await SeedAsync();

await using var provider = BuildProvider();

var cache = provider.GetRequiredService<HybridCache>();
var distributed = provider.GetRequiredService<IDistributedCache>();
var naiveEntry = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };

// A connection error surfaces here rather than part way through the first phase.
await distributed.SetStringAsync(Key("ping", 0), "ok");

log.LogInformation(
    "Redis answered. Each load phase is {Requests} reads over {Keys} keys, {Concurrency} at a time.",
    Requests,
    Keys,
    Concurrency);

var results = new List<Measurement>
{
    await MeasureAsync("no cache", Requests, Keys, Concurrency, store.LoadAsync),
    await MeasureAsync("naive cache", Requests, Keys, Concurrency, (id, token) => ReadNaiveAsync("naive", id, token)),
    await MeasureAsync("HybridCache, cold", Requests, Keys, Concurrency, (id, token) => ReadHybridAsync(cache, "hybrid", id, token)),

    // The same keys again. The phase above paid the cold start, so this one is the steady state.
    await MeasureAsync("HybridCache, warm", Requests, Keys, Concurrency, (id, token) => ReadHybridAsync(cache, "hybrid", id, token)),
    await MeasureAsync("naive, one cold key", Concurrency, 1, Concurrency, (id, token) => ReadNaiveAsync("naive-cold", id, token)),
    await MeasureAsync("HybridCache, one cold key", Concurrency, 1, Concurrency, (id, token) => ReadHybridAsync(cache, "hybrid-cold", id, token)),
};

// A second HybridCache over the same Redis. Its L1 is empty, so anything it serves came from L2.
await using var second = BuildProvider();

var secondCache = second.GetRequiredService<HybridCache>();

// Its first read would otherwise pay the TLS handshake, which is not what this phase is measuring.
await second.GetRequiredService<IDistributedCache>().SetStringAsync(Key("ping", 0), "ok");

results.Add(await MeasureAsync("HybridCache, empty L1", Keys, Keys, Keys, (id, token) => ReadHybridAsync(secondCache, "hybrid", id, token)));

Console.WriteLine();
Console.WriteLine($"{"phase",-26}{"reads",8}{"db reads",10}{"no db",9}{"reads/s",10}{"db reads/s",12}{"p50 ms",9}{"p99 ms",9}");

foreach (var result in results)
{
    Console.WriteLine(
        $"{result.Label,-26}{result.Reads,8:N0}{result.DbReads,10:N0}{result.NoDbShare,9:P1}" +
        $"{result.ReadsPerSecond,10:N0}{result.DbReadsPerSecond,12:N0}{result.P50,9:N2}{result.P99,9:N2}");
}

return 0;

async Task SeedAsync()
{
    await using var db = new QuoteDbContext(connectionString, interceptor);

    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();

    db.Quotes.AddRange(Enumerable.Range(1, Keys).Select(id => new Quote
    {
        Id = id,
        Author = $"Author {id}",
        Text = $"Quote {id}, the row the load phases read over and over.",
    }));

    await db.SaveChangesAsync();
    log.LogInformation("Seeded {Keys} quotes into {Path}.", Keys, path);
}

ServiceProvider BuildProvider()
{
    var services = new ServiceCollection();

    services.AddLogging();

    // Registering a distributed cache is the whole of the L2 wiring: HybridCache finds it.
    services.AddStackExchangeRedisCache(options => options.Configuration = redis);

    services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    });

    return services.BuildServiceProvider();
}

string Key(string phase, int id) => $"day21:{run}:{phase}:quote:{id}";

ValueTask<Quote> ReadHybridAsync(HybridCache hybrid, string phase, int id, CancellationToken cancellationToken) =>
    hybrid.GetOrCreateAsync(
        Key(phase, id),
        (store, id),
        static (state, token) => state.store.LoadAsync(state.id, token),
        cancellationToken: cancellationToken);

async ValueTask<Quote> ReadNaiveAsync(string phase, int id, CancellationToken cancellationToken)
{
    var key = Key(phase, id);
    var cached = await distributed.GetAsync(key, cancellationToken);

    if (cached is not null)
    {
        return JsonSerializer.Deserialize<Quote>(cached)!;
    }

    // Nothing here coordinates callers, so every one that missed loads the same row.
    var quote = await store.LoadAsync(id, cancellationToken);

    await distributed.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(quote), naiveEntry, cancellationToken);

    return quote;
}

async Task<Measurement> MeasureAsync(
    string label,
    int requests,
    int keys,
    int concurrency,
    Func<int, CancellationToken, ValueTask<Quote>> read)
{
    var latencies = new double[requests];
    var next = -1;

    interceptor.Reset();

    var clock = Stopwatch.StartNew();
    var workers = new Task[concurrency];

    for (var worker = 0; worker < concurrency; worker++)
    {
        workers[worker] = ReadUntilDoneAsync();
    }

    await Task.WhenAll(workers);

    clock.Stop();

    var dbReads = interceptor.Reads;

    Array.Sort(latencies);

    log.LogInformation(
        "{Label}: {Reads} read(s), {DbReads} db read(s), {Elapsed:N0} ms.",
        label,
        requests,
        dbReads,
        clock.Elapsed.TotalMilliseconds);

    return new Measurement(
        label,
        requests,
        dbReads,
        1 - ((double)dbReads / requests),
        requests / clock.Elapsed.TotalSeconds,
        dbReads / clock.Elapsed.TotalSeconds,
        latencies[(int)(0.50 * (requests - 1))],
        latencies[(int)(0.99 * (requests - 1))]);

    async Task ReadUntilDoneAsync()
    {
        int index;

        while ((index = Interlocked.Increment(ref next)) < requests)
        {
            var started = Stopwatch.GetTimestamp();

            await read(1 + (index % keys), CancellationToken.None);

            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
}

public sealed record Measurement(
    string Label,
    int Reads,
    int DbReads,
    double NoDbShare,
    double ReadsPerSecond,
    double DbReadsPerSecond,
    double P50,
    double P99);

public sealed class Quote
{
    public int Id { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class QuoteStore(string connectionString, DbReadInterceptor interceptor)
{
    public async ValueTask<Quote> LoadAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = new QuoteDbContext(connectionString, interceptor);

        return await db.Quotes.AsNoTracking().SingleAsync(quote => quote.Id == id, cancellationToken);
    }
}

public sealed class DbReadInterceptor(TimeSpan latency) : DbCommandInterceptor
{
    private int reads;

    public int Reads => Volatile.Read(ref reads);

    public void Reset() => Interlocked.Exchange(ref reads, 0);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref reads);

        // Local SQLite answers in microseconds, so this stands in for a real database's round trip.
        await Task.Delay(latency, cancellationToken);

        return result;
    }
}

public sealed class QuoteDbContext(string connectionString, DbReadInterceptor interceptor) : DbContext
{
    public DbSet<Quote> Quotes => Set<Quote>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(connectionString).AddInterceptors(interceptor);
}
