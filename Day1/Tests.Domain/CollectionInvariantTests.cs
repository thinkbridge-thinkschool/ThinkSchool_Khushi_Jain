using FluentAssertions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Tests.Domain;

public sealed class CollectionInvariantTests
{
    [Fact]
    public void EmptyName_Throws()
    {
        Action act = () => new Collection("", 1);

        act.Should().Throw<CollectionInvariantException>();
    }

    [Fact]
    public void NameOver80Characters_Throws()
    {
        var name = new string('A', 81);

        Action act = () => new Collection(name, 1);

        act.Should().Throw<CollectionInvariantException>();
    }

    [Fact]
    public void FiftyFirstItem_Throws()
    {
        var collection = new Collection("Test", 1);
        var clock = new FixedClock();

        for (var i = 1; i <= 50; i++)
            collection.AddItem(i, clock);

        Action act = () => collection.AddItem(51, clock);

        act.Should().Throw<CollectionInvariantException>();
    }

    [Fact]
    public void DuplicateQuoteId_Throws()
    {
        var collection = new Collection("Test", 1);
        var clock = new FixedClock();

        collection.AddItem(1, clock);
        Action act = () => collection.AddItem(1, clock);

        act.Should().Throw<CollectionInvariantException>();
    }

    [Fact]
    public void RemovingNonExistentItem_Throws()
    {
        var collection = new Collection("Test", 1);

        Action act = () => collection.RemoveItem(999);

        act.Should().Throw<CollectionInvariantException>();
    }

    [Fact]
    public void AddingThenRemoving_LeavesZeroItems()
    {
        var collection = new Collection("Test", 1);
        var clock = new FixedClock();

        collection.AddItem(1, clock);
        collection.RemoveItem(1);

        collection.Items.Should().BeEmpty();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
    }
}