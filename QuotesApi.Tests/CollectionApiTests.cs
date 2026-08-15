using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using QuotesApi.Contracts;

namespace QuotesApi.Tests;

public class CollectionApiTests : IntegrationTestBase
{
    private const string Owner = "owner@example.com";

    private async Task<int> CreateCollectionAsync(string name = "Favourites")
    {
        var response = await Client.PostAsJsonAsync(
            "/api/collections",
            new CreateCollectionRequest(name));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task CreateCollection_WithValidName_ReturnsCreated()
    {
        AuthorizeAs(Owner);

        var response = await Client.PostAsJsonAsync(
            "/api/collections",
            new CreateCollectionRequest("Favourites"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be("Favourites");
        body.GetProperty("ownerId").GetString().Should().Be(Owner);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CreateCollection_WithoutToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/collections",
            new CreateCollectionRequest("Favourites"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateCollection_WithNameShorterThanThreeCharacters_ReturnsBadRequest()
    {
        AuthorizeAs(Owner);

        var response = await Client.PostAsJsonAsync(
            "/api/collections",
            new CreateCollectionRequest("ab"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_ToOwnCollection_ReturnsCollectionWithTheQuote()
    {
        AuthorizeAs(Owner);
        var collectionId = await CreateCollectionAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/collections/{collectionId}/items",
            new AddCollectionItemRequest(7));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("quoteId").GetInt32().Should().Be(7);
    }

    /// <summary>
    /// The invariant the Day 1 exercise asks to demonstrate: the aggregate
    /// rejects a duplicate QuoteId and the endpoint surfaces that as a 400
    /// ProblemDetails rather than silently writing a second row.
    /// </summary>
    [Fact]
    public async Task AddItem_WithAQuoteAlreadyInTheCollection_ReturnsBadRequestProblemDetails()
    {
        AuthorizeAs(Owner);
        var collectionId = await CreateCollectionAsync();

        await Client.PostAsJsonAsync(
            $"/api/collections/{collectionId}/items",
            new AddCollectionItemRequest(7));

        var duplicate = await Client.PostAsJsonAsync(
            $"/api/collections/{collectionId}/items",
            new AddCollectionItemRequest(7));

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Collection validation failed");
        body.GetProperty("detail").GetString().Should().Contain("already in this collection");
    }

    [Fact]
    public async Task RemoveItem_ThatWasAdded_ReturnsNoContentAndLeavesCollectionEmpty()
    {
        AuthorizeAs(Owner);
        var collectionId = await CreateCollectionAsync();
        await Client.PostAsJsonAsync(
            $"/api/collections/{collectionId}/items",
            new AddCollectionItemRequest(7));

        var response = await Client.DeleteAsync($"/api/collections/{collectionId}/items/7");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var reloaded = await Client.GetFromJsonAsync<JsonElement>($"/api/collections/{collectionId}");
        reloaded.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task RemoveItem_ThatIsNotInTheCollection_ReturnsBadRequest()
    {
        AuthorizeAs(Owner);
        var collectionId = await CreateCollectionAsync();

        var response = await Client.DeleteAsync($"/api/collections/{collectionId}/items/999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetCollection_WhenItDoesNotExist_ReturnsNotFound()
    {
        var response = await Client.GetAsync("/api/collections/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The doc's alternative acceptance for cancellation: assert the operation
    /// did not complete. The server-side proof that the token actually reaches
    /// EF Core lives in CollectionRepositoryTests.
    /// </summary>
    [Fact]
    public async Task AddItem_WhenTheCallerCancels_TheOperationDoesNotComplete()
    {
        AuthorizeAs(Owner);
        var collectionId = await CreateCollectionAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Client.PostAsJsonAsync(
            $"/api/collections/{collectionId}/items",
            new AddCollectionItemRequest(7),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var reloaded = await Client.GetFromJsonAsync<JsonElement>($"/api/collections/{collectionId}");
        reloaded.GetProperty("items").GetArrayLength().Should().Be(0);
    }
}
