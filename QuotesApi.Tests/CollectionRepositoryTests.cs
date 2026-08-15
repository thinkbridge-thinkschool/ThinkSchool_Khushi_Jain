using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Tests;

public class CollectionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset AddedAt =
        new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly QuotesDbContext _db;
    private readonly CollectionRepository _repository;

    public CollectionRepositoryTests()
    {
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new QuotesDbContext(options);
        _db.Database.EnsureCreated();

        _repository = new CollectionRepository(_db);
    }

    /// <summary>
    /// CollectionItem is mapped as an owned type, so its rows are written and
    /// read as part of the aggregate rather than through a DbSet of their own.
    /// This is the test that would fail if the OwnsMany mapping regressed.
    /// </summary>
    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTripsOwnedItems()
    {
        var collection = Collection.Create("Favourites", "owner-1");
        collection.AddItem(7, AddedAt);
        collection.AddItem(9, AddedAt);

        var saved = await _repository.AddAsync(collection, CancellationToken.None);
        _db.ChangeTracker.Clear();

        var loaded = await _repository.GetByIdAsync(saved.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Favourites");
        loaded.OwnerId.Should().Be("owner-1");
        loaded.Items.Select(i => i.QuoteId).Should().BeEquivalentTo([7, 9]);
        loaded.Items.Should().OnlyContain(i => i.AddedAt == AddedAt);
    }

    [Fact]
    public async Task UpdateAsync_AfterRemoveItem_DeletesTheOwnedRow()
    {
        var collection = Collection.Create("Favourites", "owner-1");
        collection.AddItem(7, AddedAt);
        collection.AddItem(9, AddedAt);
        var saved = await _repository.AddAsync(collection, CancellationToken.None);

        saved.RemoveItem(7);
        await _repository.UpdateAsync(saved, CancellationToken.None);
        _db.ChangeTracker.Clear();

        var loaded = await _repository.GetByIdAsync(saved.Id, CancellationToken.None);

        loaded!.Items.Select(i => i.QuoteId).Should().BeEquivalentTo([9]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheAggregateAndItsItems()
    {
        var collection = Collection.Create("Favourites", "owner-1");
        collection.AddItem(7, AddedAt);
        var saved = await _repository.AddAsync(collection, CancellationToken.None);

        var deleted = await _repository.DeleteAsync(saved.Id, CancellationToken.None);
        _db.ChangeTracker.Clear();

        deleted.Should().BeTrue();
        (await _repository.GetByIdAsync(saved.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenCollectionDoesNotExist_ReturnsFalse()
    {
        var result = await _repository.DeleteAsync(999999, CancellationToken.None);

        result.Should().BeFalse();
    }

    /// <summary>
    /// The token is passed through to EF Core rather than accepted and ignored.
    /// A repository that dropped it would return a result here instead of
    /// throwing, which is the failure this guards against.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_WithACancelledToken_DoesNotRunTheQuery()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _repository.GetByIdAsync(1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AddAsync_WithACancelledToken_DoesNotPersist()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collection = Collection.Create("Favourites", "owner-1");

        var act = () => _repository.AddAsync(collection, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        _db.ChangeTracker.Clear();
        (await _db.Collections.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
