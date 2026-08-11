using Microsoft.EntityFrameworkCore;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

builder.Services.AddDbContext<QuotesDbContext>(options =>
    options.UseSqlite("Data Source=quotes.db"));

builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();
builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();

var app = builder.Build();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var logger = context.RequestServices
            .GetRequiredService<ILogger<Program>>();

        logger.LogError(
            context.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?
                .Error,
            "Unhandled API exception");

        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.")
            .ExecuteAsync(context);
    });
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
    await db.Database.MigrateAsync();
}

MapQuoteEndpoints(app);
MapCollectionEndpoints(app);

app.Run();

static void MapQuoteEndpoints(WebApplication app)
{
    var group = app.MapGroup("/api/quotes");

    group.MapGet("/", async (
        int? page,
        int? size,
        IQuoteRepository repository,
        CancellationToken cancellationToken) =>
    {
        var currentPage = page.GetValueOrDefault(1);
        var pageSize = size.GetValueOrDefault(10);

        if (currentPage < 1 || pageSize is < 1 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["page/size"] = ["Page must be >= 1 and size must be between 1 and 100."]
            });
        }

        var result = await repository.GetPagedAsync(
            currentPage, pageSize, cancellationToken);

        return Results.Ok(new
        {
            page = currentPage,
            size = pageSize,
            total = result.Total,
            items = result.Items
        });
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
        IQuoteRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var errors = Validate(request);

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var quote = await repository.AddAsync(
            new Quote
            {
                Author = request.Author.Trim(),
                Text = request.Text.Trim()
            },
            cancellationToken);

        logger.LogInformation("Created quote {QuoteId}", quote.Id);

        return Results.Created($"/api/quotes/{quote.Id}", quote);
    });

    group.MapDelete("/{id:int}", async (
        int id,
        IQuoteRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var deleted = await repository.DeleteAsync(id, cancellationToken);

        if (!deleted)
            return Results.NotFound();

        logger.LogInformation("Deleted quote {QuoteId}", id);

        return Results.NoContent();
    });
}

static Dictionary<string, string[]> Validate(CreateQuoteRequest request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.Author))
        errors["author"] = ["Author is required."];

    if (string.IsNullOrWhiteSpace(request.Text))
        errors["text"] = ["Text is required."];

    if (request.Author?.Length > 100)
        errors["author"] = ["Author must be 100 characters or fewer."];

    if (request.Text?.Length > 500)
        errors["text"] = ["Text must be 500 characters or fewer."];

    return errors;
}
static void MapCollectionEndpoints(WebApplication app)
{
    var group = app.MapGroup("/api/collections");

    group.MapPost("/", async (
        CreateCollectionRequest request,
        ICollectionRepository repository,
        CancellationToken cancellationToken) =>
    {
        var collection = new Collection(
            request.Name ?? string.Empty,
            request.OwnerId);

        await repository.AddAsync(
            collection,
            cancellationToken);

        return Results.Created(
            $"/api/collections/{collection.Id}",
            collection);
    });

    group.MapGet("/{id:int}", async (
        int id,
        ICollectionRepository repository,
        CancellationToken cancellationToken) =>
    {
        var collection = await repository.GetByIdAsync(
            id,
            cancellationToken);

        return collection is null
            ? Results.NotFound()
            : Results.Ok(collection);
    });

    group.MapPost("/{id:int}/items", async (
        int id,
        AddCollectionItemRequest request,
        ICollectionRepository repository,
        IClock clock,
        CancellationToken cancellationToken) =>
    {
        var collection = await repository.GetByIdAsync(
            id,
            cancellationToken);

        if (collection is null)
            return Results.NotFound();

        try
{
    collection.AddItem(request.QuoteId, clock);

    await repository.UpdateAsync(
        collection,
        cancellationToken);

    return Results.Ok(collection);
}
catch (CollectionInvariantException ex)
{
    return Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Collection invariant violated",
        detail: ex.Message);
}
    });

    group.MapDelete("/{id:int}/items/{quoteId:int}", async (
        int id,
        int quoteId,
        ICollectionRepository repository,
        CancellationToken cancellationToken) =>
    {
        var collection = await repository.GetByIdAsync(
            id,
            cancellationToken);

        if (collection is null)
            return Results.NotFound();

        collection.RemoveItem(quoteId);

        await repository.UpdateAsync(
            collection,
            cancellationToken);

        return Results.Ok(collection);
    });

    group.MapDelete("/{id:int}", async (
        int id,
        ICollectionRepository repository,
        CancellationToken cancellationToken) =>
    {
        var deleted = await repository.DeleteAsync(
            id,
            cancellationToken);

        return deleted
            ? Results.NoContent()
            : Results.NotFound();
    });
}