using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit.Abstractions;

namespace QuotesApi.Tests;

public sealed class ReadModelTests(ITestOutputHelper output) : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _log = [];

    [Fact]
    public async Task WriteTracksTheAggregate_ReadProjectsTheScreenInOneStatement()
    {
        _connection.Open();

        await using var write = NewContext();
        write.Database.EnsureCreated();

        var quote = Quote.Create("Grace Hopper", "A ship in port is safe.", "seed-owner");
        var collection = Collection.Create("Favourites", "owner@example.com");
        write.Quotes.Add(quote);
        write.Collections.Add(collection);
        await write.SaveChangesAsync();

        var added = await new AddQuoteToCollectionHandler(
                new CollectionRepository(write),
                new FakeClock())
            .HandleAsync(collection.Id, quote.Id, CancellationToken.None);

        added.Should().BeTrue();
        write.ChangeTracker.Entries().Should().NotBeEmpty();

        await using var read = NewContext();
        _log.Clear();

        var details = await new CollectionDetailsQuery(read)
            .RunAsync(collection.Id, CancellationToken.None);

        output.WriteLine(_log[^1]);

        details!.Name.Should().Be("Favourites");
        details.ItemCount.Should().Be(1);
        details.Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { quote.Author, quote.Text });

        _log.Should().HaveCount(1);
        read.ChangeTracker.Entries().Should().BeEmpty();
    }

    private QuotesDbContext NewContext() =>
        new(new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .LogTo(_log.Add, [RelationalEventId.CommandExecuted])
            .Options);

    public void Dispose() => _connection.Dispose();
}
