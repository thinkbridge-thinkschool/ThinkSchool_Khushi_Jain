# Day 12 — Read models + CQRS-lite

One feature split in two: adding a quote to a collection is a command, and the collection detail
screen is a query. No MediatR, no event sourcing — just two classes and two separate paths.

| | Write path | Read path |
|---|---|---|
| Endpoint | `POST /api/collections/{id}/items` | `GET /api/collections/{id}` |
| Class | [`AddQuoteToCollectionHandler`](../../QuotesApi/Services/AddQuoteToCollectionHandler.cs) | [`CollectionDetailsQuery`](../../QuotesApi/Services/CollectionDetailsQuery.cs) |
| Model | the `Collection` aggregate, tracked | `CollectionDetails`, a projection |
| Talks to | `ICollectionRepository` | `QuotesDbContext` directly |
| Returns | `204 No Content` | the read model |

## The command handler

It loads the aggregate, lets the aggregate enforce its own invariants, and saves. It returns a
`bool` for "did that collection exist" and nothing else.

```csharp
public async Task<bool> HandleAsync(
    int collectionId,
    int quoteId,
    CancellationToken cancellationToken)
{
    var collection = await repository.GetByIdAsync(collectionId, cancellationToken);

    if (collection is null)
    {
        return false;
    }

    collection.AddItem(quoteId, clock.UtcNow);

    await repository.UpdateAsync(collection, cancellationToken);

    return true;
}
```

## The read model

The aggregate stores membership as bare quote ids, which is right for the write side and useless
for a screen. The read model flattens the quote's author and text onto each row and adds the count
the header shows.

```csharp
public sealed record CollectionDetails(
    int Id,
    string Name,
    string OwnerId,
    int ItemCount,
    IReadOnlyList<CollectionDetailsItem> Items);

public sealed record CollectionDetailsItem(
    int QuoteId,
    string Author,
    string Text,
    DateTimeOffset AddedAt);
```

Built in one statement, joining `Quotes` and skipping soft-deleted ones:

```csharp
db.Collections
    .Where(collection => collection.Id == id)
    .Select(collection => new CollectionDetails(
        collection.Id,
        collection.Name,
        collection.OwnerId,
        collection.Items.Count,
        (from item in collection.Items
         join quote in db.Quotes on item.QuoteId equals quote.Id
         where !quote.IsDeleted
         // SQLite cannot ORDER BY a DateTimeOffset; the owned key is add order.
         orderby EF.Property<int>(item, "Id")
         select new CollectionDetailsItem(
             quote.Id,
             quote.Author,
             quote.Text,
             item.AddedAt))
        .ToList()))
    .FirstOrDefaultAsync(cancellationToken);
```

The SQL that comes out, printed by
[`ReadModelTests`](../../QuotesApi.Tests/ReadModelTests.cs):

```sql
SELECT "c2"."Id", "c2"."Name", "c2"."OwnerId", "c2"."c", "s"."Id", "s"."Author", "s"."Text", "s"."AddedAt", "s"."Id0"
FROM (
    SELECT "c"."Id", "c"."Name", "c"."OwnerId", (
        SELECT COUNT(*)
        FROM "CollectionItems" AS "c0"
        WHERE "c"."Id" = "c0"."CollectionId") AS "c"
    FROM "Collections" AS "c"
    WHERE "c"."Id" = @id
    LIMIT 1
) AS "c2"
LEFT JOIN (
    SELECT "q"."Id", "q"."Author", "q"."Text", "c1"."AddedAt", "c1"."Id" AS "Id0", "c1"."CollectionId"
    FROM "CollectionItems" AS "c1"
    INNER JOIN "Quotes" AS "q" ON "c1"."QuoteId" = "q"."Id"
    WHERE NOT ("q"."IsDeleted")
) AS "s" ON "c2"."Id" = "s"."CollectionId"
ORDER BY "c2"."Id", "s"."Id0"
```

One statement, and only the nine columns the screen renders. No `AsNoTracking()` is needed: the
result is a record, not an entity, so the change tracker stays empty — the test asserts that, and
asserts the write path's tracker does not.

## What got simpler

The command no longer has to answer "what should the screen look like now?" — it stopped loading and
re-serialising the aggregate just to build a response, and the read stopped being limited to what the
aggregate happens to hold, so quote text arrives in the same query instead of a second one.

## Notes

- Only this one feature is split. `POST /api/collections` and `DELETE .../items/{quoteId}` are
  untouched.
- `POST .../items` used to return `200` with the aggregate's shape; it now returns `204`.
- `orderby` uses the owned type's shadow key rather than `AddedAt` because SQLite cannot
  `ORDER BY` a `DateTimeOffset`. The shadow key is insertion order, which is the same thing here.

## Run it

```bash
dotnet test QuotesApi.Tests/QuotesApi.Tests.csproj --filter "FullyQualifiedName~ReadModelTests" --logger "console;verbosity=detailed"
```
