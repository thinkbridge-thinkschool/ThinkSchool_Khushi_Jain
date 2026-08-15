using FluentAssertions;
using QuotesApi.Models;

namespace Tests.Domain;

/// <summary>
/// Pure aggregate tests: no DbContext, no host, no fixtures. Every timestamp is
/// supplied as a value, which is why nothing here needs a clock or a substitute.
/// </summary>
public sealed class CollectionInvariantTests
{
    private const string OwnerId = "owner-1";

    private static readonly DateTimeOffset AddedAt =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithEmptyName_Throws()
    {
        Action act = () => Collection.Create(string.Empty, OwnerId);

        act.Should().Throw<CollectionDomainException>();
    }

    [Fact]
    public void Create_WithNameLongerThan80Characters_Throws()
    {
        Action act = () => Collection.Create(new string('a', 81), OwnerId);

        act.Should().Throw<CollectionDomainException>();
    }

    [Fact]
    public void AddItem_WhenCollectionAlreadyHolds50Items_Throws()
    {
        var collection = Collection.Create("Favourites", OwnerId);
        for (var quoteId = 1; quoteId <= Collection.MaximumItems; quoteId++)
            collection.AddItem(quoteId, AddedAt);

        Action act = () => collection.AddItem(51, AddedAt);

        act.Should().Throw<CollectionDomainException>();
    }

    [Fact]
    public void AddItem_WithDuplicateQuoteId_Throws()
    {
        var collection = Collection.Create("Favourites", OwnerId);
        collection.AddItem(7, AddedAt);

        Action act = () => collection.AddItem(7, AddedAt);

        act.Should().Throw<CollectionDomainException>();
    }

    [Fact]
    public void RemoveItem_WhenQuoteIsNotInCollection_Throws()
    {
        var collection = Collection.Create("Favourites", OwnerId);

        Action act = () => collection.RemoveItem(99);

        act.Should().Throw<CollectionDomainException>();
    }

    [Fact]
    public void AddItem_ThenRemoveItem_LeavesCollectionEmpty()
    {
        var collection = Collection.Create("Favourites", OwnerId);

        collection.AddItem(7, AddedAt);
        collection.RemoveItem(7);

        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithNameOfExactly3Characters_Succeeds()
    {
        var collection = Collection.Create("abc", OwnerId);

        collection.Name.Should().Be("abc");
    }

    [Fact]
    public void Create_WithNameOfExactly80Characters_Succeeds()
    {
        var collection = Collection.Create(new string('a', 80), OwnerId);

        collection.Name.Should().HaveLength(80);
    }

    [Fact]
    public void AddItem_ForThe50thItem_Succeeds()
    {
        var collection = Collection.Create("Favourites", OwnerId);

        for (var quoteId = 1; quoteId <= Collection.MaximumItems; quoteId++)
            collection.AddItem(quoteId, AddedAt);

        collection.Items.Should().HaveCount(Collection.MaximumItems);
    }
}
