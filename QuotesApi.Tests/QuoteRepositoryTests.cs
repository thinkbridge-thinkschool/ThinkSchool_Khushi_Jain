using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Repositories;

namespace QuotesApi.Tests;

public class QuoteRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly QuotesDbContext _db;
    private readonly QuoteRepository _repository;

    public QuoteRepositoryTests()
    {
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new QuotesDbContext(options);
        _db.Database.EnsureCreated();

        _repository = new QuoteRepository(_db);
    }

    [Fact]
    public async Task DeleteAsync_QuoteDoesNotExist_ReturnsFalse()
    {
        // The DELETE endpoint already checks existence via GetByIdAsync before
        // ever calling DeleteAsync, so this branch is never reached through the
        // API today. It's still part of the repository's public contract, so
        // it's tested directly here rather than through an HTTP round trip.
        var result = await _repository.DeleteAsync(999999, CancellationToken.None);

        result.Should().BeFalse();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
