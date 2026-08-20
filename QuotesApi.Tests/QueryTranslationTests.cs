using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuotesApi.Data;
using QuotesApi.Models;
using Xunit.Abstractions;

namespace QuotesApi.Tests;

public sealed class QueryTranslationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _log = [];
    private readonly QuotesDbContext _db;

    public QueryTranslationTests(ITestOutputHelper output)
    {
        _output = output;
        _connection.Open();

        _db = new QuotesDbContext(new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .LogTo(_log.Add, [RelationalEventId.CommandExecuted])
            .EnableSensitiveDataLogging()
            .Options);

        _db.Database.EnsureCreated();
        _db.Quotes.Add(Quote.Create("Ada Lovelace", "The Analytical Engine weaves algebraic patterns.", "seed-owner"));
        _db.Quotes.Add(Quote.Create("Grace Hopper", "The most damaging phrase is: we've always done it this way.", "seed-owner"));
        _db.SaveChanges();
        _log.Clear();
    }

    [Fact]
    public async Task EntityQuery_SelectsEveryMappedColumn()
    {
        await ListQuery().ToListAsync();

        var command = LastCommand();
        _output.WriteLine(command);

        command.Should().Contain("\"q\".\"Text\"");
        command.Should().Contain("\"q\".\"IsDeleted\"");
        command.Should().Contain("\"q\".\"OwnerId\"");
    }

    [Fact]
    public async Task ProjectedQuery_SelectsOnlyTheColumnsTheDtoNeeds()
    {
        await ListQuery().Select(q => new QuoteSummary(q.Id, q.Author)).ToListAsync();

        var command = LastCommand();
        _output.WriteLine(command);

        command.Should().Contain("\"q\".\"Author\"");
        command.Should().NotContain("\"q\".\"Text\"");
        command.Should().NotContain("\"q\".\"OwnerId\"");
    }

    [Fact]
    public async Task CaseInsensitiveEquals_FailsToTranslate_AndIsFixedByLoweringInTheQuery()
    {
        var author = "ada lovelace";

        var clientEvaluated = () => _db.Quotes
            .Where(q => q.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
            .ToListAsync();

        var thrown = await clientEvaluated.Should().ThrowAsync<InvalidOperationException>();
        _output.WriteLine(thrown.Which.Message);

        var translated = await _db.Quotes
            .Where(q => q.Author.ToLower() == author)
            .AsNoTracking()
            .ToListAsync();

        var command = LastCommand();
        _output.WriteLine(command);

        translated.Should().HaveCount(1);
        command.Should().Contain("lower(\"q\".\"Author\")");
        command.Should().Contain("'ada lovelace'");
    }

    private IQueryable<Quote> ListQuery() =>
        _db.Quotes.Where(q => !q.IsDeleted).OrderBy(q => q.Id).AsNoTracking();

    // Drops LogTo's leading category line, keeping the parameter header and the SQL.
    private string LastCommand() =>
        string.Join(
            Environment.NewLine,
            _log[^1].Split(Environment.NewLine).Skip(1).Select(line => line.TrimStart()));

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

internal sealed record QuoteSummary(int Id, string Author);
