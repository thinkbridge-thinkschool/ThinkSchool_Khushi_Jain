using System.Security.Claims;
using QuotesApi.Authorization;
using QuotesApi.Contracts;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Time;

namespace QuotesApi.Controllers;

public static class CollectionController
{
    public static void MapCollectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/collections");

        group.MapPost("/", async (
            CreateCollectionRequest request,
            ClaimsPrincipal user,
            ICollectionRepository repository,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            Collection collection;

            try
            {
                collection = Collection.Create(request.Name, user.GetSubjectId()!);
            }
            catch (CollectionDomainException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Collection validation failed",
                    detail: ex.Message);
            }

            collection = await repository.AddAsync(collection, cancellationToken);

            logger.LogInformation("Created collection {CollectionId}", collection.Id);

            return Results.Created(
                $"/api/collections/{collection.Id}",
                ToResponse(collection));
        }).RequireAuthorization("can-edit-quotes");

        group.MapGet("/{id:int}", async (
            int id,
            ICollectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            return collection is null
                ? Results.NotFound()
                : Results.Ok(ToResponse(collection));
        });

        group.MapPost("/{id:int}/items", async (
            int id,
            AddCollectionItemRequest request,
            ICollectionRepository repository,
            IClock clock,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            if (collection is null)
            {
                return Results.NotFound();
            }

            try
            {
                collection.AddItem(request.QuoteId, clock.UtcNow);
            }
            catch (CollectionDomainException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Collection validation failed",
                    detail: ex.Message);
            }

            await repository.UpdateAsync(collection, cancellationToken);

            logger.LogInformation(
                "Added quote {QuoteId} to collection {CollectionId}",
                request.QuoteId,
                id);

            return Results.Ok(ToResponse(collection));
        }).RequireAuthorization("can-edit-quotes");

        group.MapDelete("/{id:int}/items/{quoteId:int}", async (
            int id,
            int quoteId,
            ICollectionRepository repository,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            if (collection is null)
            {
                return Results.NotFound();
            }

            try
            {
                collection.RemoveItem(quoteId);
            }
            catch (CollectionDomainException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Collection validation failed",
                    detail: ex.Message);
            }

            await repository.UpdateAsync(collection, cancellationToken);

            logger.LogInformation(
                "Removed quote {QuoteId} from collection {CollectionId}",
                quoteId,
                id);

            return Results.NoContent();
        }).RequireAuthorization("can-edit-quotes");
    }

    private static object ToResponse(Collection collection) => new
    {
        collection.Id,
        collection.Name,
        collection.OwnerId,
        items = collection.Items.Select(item => new
        {
            item.QuoteId,
            item.AddedAt
        })
    };
}
