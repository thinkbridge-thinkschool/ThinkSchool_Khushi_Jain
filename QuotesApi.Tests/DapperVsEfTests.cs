using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;
using Xunit.Abstractions;

namespace QuotesApi.Tests;

public sealed class DapperVsEfTests : IDisposable
{
    private const int CollectionCount = 200;
    private const int ItemsPerCollection = 25;
    private const int Iterations = 500;

    private readonly ITestOutputHelper _output;
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"day12-dapper-{Guid.NewGuid():N}.db");
    private readonly List<string> _log = [];
    private readonly QuotesDbContext _db;
    private readonly int[] _ids;

    public DapperVsEfTests(ITestOutputHelper output)
    {
        _output = output;

        _db = new QuotesDbContext(new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .LogTo(_log.Add, [RelationalEventId.CommandExecuted])
            .Options);
        _db.Database.EnsureCreated();

        var quotes = Enumerable.Range(0, CollectionCount * ItemsPerCollection)
            .Select(i => Quote.Create($"Author {i % 200}", $"Quote text number {i}.", "seed-owner"))
            .ToList();
        _db.Quotes.AddRange(quotes);
        _db.SaveChanges();

        var addedAt = new FakeClock().UtcNow;
        var collections = new List<Collection>();

        for (var c = 0; c < CollectionCount; c++)
        {
            var collection = Collection.Create($"Collection {c}", "owner@example.com");

            for (var i = 0; i < ItemsPerCollection; i++)
            {
                collection.AddItem(quotes[(c * ItemsPerCollection) + i].Id, addedAt.AddSeconds(i));
            }

            collections.Add(collection);
        }

        _db.Collections.AddRange(collections);
        _db.SaveChanges();

        _ids = collections.Select(collection => collection.Id).ToArray();
    }

    [Fact]
    public async Task Dapper_ReturnsTheSameReadModelAsEf_AndIsTimedAgainstIt()
    {
        var ef = new CollectionDetailsQuery(_db);
        var dapper = new CollectionDetailsDapperQuery(_db);

        var expected = await ef.RunAsync(_ids[7], CancellationToken.None);
        var actual = await dapper.RunAsync(_ids[7], CancellationToken.None);

        expected!.Items.Should().HaveCount(ItemsPerCollection);
        actual.Should().BeEquivalentTo(expected);

        var efSql = LastSql();
        var connection = _db.Database.GetDbConnection();

        _output.WriteLine(
            $"{CollectionCount} collections x {ItemsPerCollection} items, " +
            $"{Iterations} iterations, Release");

        foreach (var (label, sql) in new[]
                 {
                     ("EF", efSql),
                     ("Dapper", CollectionDetailsDapperQuery.Sql)
                 })
        {
            _output.WriteLine($"--- {label} plan ---");

            foreach (var row in await connection.QueryAsync(
                $"EXPLAIN QUERY PLAN {sql}", new { id = _ids[7] }))
            {
                _output.WriteLine("  " + (string)row.detail);
            }
        }

        var efUs = await MeasureAsync(id => ef.RunAsync(id, CancellationToken.None));
        var dapperUs = await MeasureAsync(id => dapper.RunAsync(id, CancellationToken.None));

        // Same mapper, EF's SQL: separates the generated shape from the mapper.
        var efSqlViaDapperUs = await MeasureAsync(async id =>
        {
            await connection.QueryAsync(efSql, new { id });
            return null;
        });

        _output.WriteLine($"EF                 {efUs:F1} us/call");
        _output.WriteLine($"Dapper             {dapperUs:F1} us/call   {efUs / dapperUs:F1}x faster");
        _output.WriteLine($"EF SQL via Dapper  {efSqlViaDapperUs:F1} us/call");
    }

    private async Task<double> MeasureAsync(Func<int, Task<CollectionDetails?>> run)
    {
        for (var i = 0; i < 50; i++)
        {
            await run(_ids[i % _ids.Length]);
        }

        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < Iterations; i++)
        {
            await run(_ids[i % _ids.Length]);
        }

        return stopwatch.Elapsed.TotalMilliseconds * 1000 / Iterations;
    }

    private string LastSql() =>
        string.Join(
            Environment.NewLine,
            _log[^1]
                .Split(Environment.NewLine)
                .SkipWhile(line => !line.TrimStart().StartsWith("SELECT"))
                .Select(line => line.TrimStart()));

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
