using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Authorization;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Controllers;

public static class QuoteController
{
    public static void MapQuoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/quotes");

        group.MapGet("/", async (
            int? page,
            int? size,
            IQuoteRepository repository,
            QuotesDbContext db,
            ActivitySource activitySource,
            CancellationToken cancellationToken) =>
        {
            var currentPage = page.GetValueOrDefault(1);
            var pageSize = size.GetValueOrDefault(10);

            if (currentPage < 1 || pageSize is < 1 or > 100)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["page/size"] =
                        [
                            "Page must be >= 1 and size must be between 1 and 100."
                        ]
                    });
            }

            var result = await repository.GetPagedAsync(
                currentPage,
                pageSize,
                cancellationToken);

            // Flags whether each quote's owner account still exists, so the
            // client can grey out quotes left behind by a deleted account.
            // Fetches every owner in a single batched query instead of one
            // query per quote.
            using var activity = activitySource.StartActivity("check-owner-status");
            activity?.SetTag("owner_checks.count", result.Items.Count);

            var ownerIds = result.Items
                .Where(q => q.OwnerId is not null)
                .Select(q => q.OwnerId!)
                .Distinct()
                .ToList();

            var activeOwners = await db.Users
                .Where(u => ownerIds.Contains(u.Email))
                .Select(u => u.Email)
                .ToHashSetAsync(cancellationToken);

            var itemsWithOwnerStatus = result.Items.Select(quote => new
            {
                quote.Id,
                quote.Author,
                quote.Text,
                quote.OwnerId,
                ownerActive = quote.OwnerId is not null && activeOwners.Contains(quote.OwnerId)
            });

            return Results.Ok(new
            {
                page = currentPage,
                size = pageSize,
                total = result.Total,
                items = itemsWithOwnerStatus
            });
        });

        group.MapGet("/by-author", async (
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            // One query, two columns, grouped in memory.
            var rows = await db.Quotes
                .Where(q => !q.IsDeleted)
                .OrderBy(q => q.Author)
                .Select(q => new { q.Author, q.Text })
                .ToListAsync(cancellationToken);

            var byAuthor = rows
                .GroupBy(row => row.Author)
                .Select(group => new { author = group.Key, quotes = group.Select(row => row.Text) });

            return Results.Ok(byAuthor);
        });

        group.MapGet("/{id:int}", async (
            int id,
            IQuoteRepository repository,
            CancellationToken cancellationToken) =>
        {
            var quote = await repository.GetByIdAsync(id, cancellationToken);

            return quote is null
                ? Results.NotFound()
                : Results.Ok(quote);
        });

        group.MapPost("/", async (
            CreateQuoteRequest request,
            ClaimsPrincipal user,
            IQuoteRepository repository,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var subjectId = user.GetSubjectId();

            // An authenticated token without a subject claim cannot own a
            // quote. That is an authentication problem, not a bad request.
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            Quote quote;

            try
            {
                quote = Quote.Create(
                    request.Author,
                    request.Text,
                    subjectId);
            }
            catch (QuoteDomainException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Quote validation failed",
                    detail: ex.Message);
            }

            quote = await repository.AddAsync(quote, cancellationToken);

            logger.LogInformation("Created quote {QuoteId}", quote.Id);

            return Results.Created($"/api/quotes/{quote.Id}", quote);
        }).RequireAuthorization("can-edit-quotes");

        group.MapDelete("/{id:int}", async (
            int id,
            ClaimsPrincipal user,
            IQuoteRepository repository,
            IAuthorizationService authorizationService,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var quote = await repository.GetByIdAsync(id, cancellationToken);

            if (quote is null)
            {
                return Results.NotFound();
            }

            var authorizationResult = await authorizationService.AuthorizeAsync(
                user,
                quote,
                new CanModifyOwnQuoteRequirement());

            if (!authorizationResult.Succeeded)
            {
                return Results.Forbid();
            }

            await repository.DeleteAsync(id, cancellationToken);

            logger.LogInformation("Deleted quote {QuoteId}", id);

            return Results.NoContent();
        }).RequireAuthorization("can-edit-quotes");
    }
}
