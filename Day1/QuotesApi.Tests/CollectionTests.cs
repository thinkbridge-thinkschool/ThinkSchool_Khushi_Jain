using QuotesApi.Models;

namespace QuotesApi.Tests;

public class CollectionTests
{
    [Fact]
    public void AddItem_UsesInjectedClock()
    {
        var expectedTime = new DateTimeOffset(
            2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        var clock = new FakeClock(expectedTime);

        var collection = new Collection(
            "Test Collection",
            1);

        collection.AddItem(1, clock);

        var item = Assert.Single(collection.Items);

        Assert.Equal(1, item.QuoteId);
        Assert.Equal(expectedTime, item.AddedAt);
    }
}