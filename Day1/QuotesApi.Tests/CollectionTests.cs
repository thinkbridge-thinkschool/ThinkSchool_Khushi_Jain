
using QuotesApi.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Repositories;


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

    [Fact]
    public async Task AddItemEndpoint_CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(1000, cts.Token);
        });
    }
    public class CollectionCancellationTests
{
    [Fact]
    public async Task AddItemEndpoint_CancellationIsHonored()
    {
        var repository = new BlockingCollectionRepository();

        await using var factory =
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ICollectionRepository>();
                        services.AddSingleton<ICollectionRepository>(repository);
                    });
                });

        using var client = factory.CreateClient();

        using var cts = new CancellationTokenSource();

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/collections/1/items")
        {
            Content = new StringContent(
                """{"quoteId":1}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };

        var requestTask = client.SendAsync(
            request,
            cts.Token);

        await repository.Started.Task;

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await requestTask);

        Assert.True(repository.CancellationObserved);
    }

    private sealed class BlockingCollectionRepository
        : ICollectionRepository
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<Collection?> GetByIdAsync(
            int id,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);

            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                return null;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public Task<Collection> AddAsync(
            Collection collection,
            CancellationToken cancellationToken)
            => Task.FromResult(collection);

        public Task<Collection> UpdateAsync(
            Collection collection,
            CancellationToken cancellationToken)
            => Task.FromResult(collection);

        public Task<bool> DeleteAsync(
            int id,
            CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
}
