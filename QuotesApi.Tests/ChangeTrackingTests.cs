using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;
using Xunit.Abstractions;

namespace QuotesApi.Tests;

public sealed class ChangeTrackingTests : IDisposable
{
    private const int RowCount = 10_000;
    private const int Iterations = 5;

    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<QuotesDbContext> _options;

    public ChangeTrackingTests(ITestOutputHelper output)
    {
        _output = output;
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();

        for (var i = 1; i <= RowCount; i++)
        {
            db.Quotes.Add(Quote.Create($"Author {i % 100}", $"Quote number {i}", "seed-owner"));
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task TrackingQuery_TracksEveryRow_AndResolvesARepeatQueryToTheSameInstances()
    {
        using var db = NewContext();

        var first = await db.Quotes.ToListAsync();

        first.Should().HaveCount(RowCount);
        db.ChangeTracker.Entries<Quote>().Should().HaveCount(RowCount);
        db.Entry(first[0]).State.Should().Be(EntityState.Unchanged);

        var second = await db.Quotes.ToListAsync();

        second[0].Should().BeSameAs(first[0]);
        db.ChangeTracker.Entries<Quote>().Should().HaveCount(RowCount);
    }

    [Fact]
    public async Task AsNoTrackingQuery_TracksNothing_AndReturnsFreshInstancesEachTime()
    {
        using var db = NewContext();

        var first = await db.Quotes.AsNoTracking().ToListAsync();
        var second = await db.Quotes.AsNoTracking().ToListAsync();

        first.Should().HaveCount(RowCount);
        db.ChangeTracker.Entries<Quote>().Should().BeEmpty();
        second[0].Should().NotBeSameAs(first[0]);
    }

    [Fact]
    public async Task TenThousandRowRead_IsFasterAndLeaner_WithoutTracking()
    {
        await MeasureAsync(tracking: true);
        await MeasureAsync(tracking: false);

        var tracked = await MeasureAsync(tracking: true);
        var untracked = await MeasureAsync(tracking: false);

        _output.WriteLine($"{RowCount:N0} rows, mean of {Iterations} reads after warm-up");
        _output.WriteLine($"tracking      {tracked.Milliseconds,7:F1} ms   {tracked.Bytes / 1024.0,8:F0} KB");
        _output.WriteLine($"AsNoTracking  {untracked.Milliseconds,7:F1} ms   {untracked.Bytes / 1024.0,8:F0} KB");

        untracked.Bytes.Should().BeLessThan(tracked.Bytes);
        untracked.Milliseconds.Should().BeLessThan(tracked.Milliseconds);
    }

    private async Task<(double Milliseconds, long Bytes)> MeasureAsync(bool tracking)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < Iterations; i++)
        {
            using var db = NewContext();
            IQueryable<Quote> query = tracking ? db.Quotes : db.Quotes.AsNoTracking();
            await query.ToListAsync();
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        return (stopwatch.Elapsed.TotalMilliseconds / Iterations, allocated / Iterations);
    }

    private QuotesDbContext NewContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
