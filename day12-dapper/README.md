# Day 12 — When to reach for Dapper

The read path from [piece 1](../day12-read-models/) reimplemented with Dapper, and measured against
it. Same database, same read model, same process.

The collection detail query is the right candidate: it is the only read here that a screen hits on
every page load, and piece 1 had already tightened it to a single statement — so whatever was left
would be the mapper or the generated shape, not an obvious mistake in the LINQ.

| | EF | Dapper |
|---|---|---|
| Class | [`CollectionDetailsQuery`](../QuotesApi/Services/CollectionDetailsQuery.cs) | [`CollectionDetailsDapperQuery`](../QuotesApi/Services/CollectionDetailsDapperQuery.cs) |
| SQL | generated from LINQ | hand-written, a `const` on the class |
| Returns | `CollectionDetails` | the same `CollectionDetails` |

The test asserts the two return equivalent read models before it times them, so this is a
like-for-like comparison and not two different queries.

## The two implementations

EF — LINQ, and EF decides the SQL:

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
         orderby EF.Property<int>(item, "Id")
         select new CollectionDetailsItem(
             quote.Id,
             quote.Author,
             quote.Text,
             item.AddedAt))
        .ToList()))
    .FirstOrDefaultAsync(cancellationToken);
```

Dapper — the SQL is mine, and so is the mapping:

```csharp
var rows = (await db.Database.GetDbConnection().QueryAsync<Row>(
    new CommandDefinition(Sql, new { id }, cancellationToken: cancellationToken)))
    .AsList();

if (rows.Count == 0)
{
    return null;
}

return new CollectionDetails(
    rows[0].Id,
    rows[0].Name,
    rows[0].OwnerId,
    rows.Count(row => row.ItemId is not null),
    rows
        .Where(row => row.QuoteId is not null)
        .Select(row => new CollectionDetailsItem(
            row.QuoteId!.Value,
            row.Author!,
            row.Text!,
            DateTimeOffset.Parse(row.AddedAt!, CultureInfo.InvariantCulture)))
        .ToList());
```

The flat row it maps into is where the hidden work shows up — every item column is nullable because
of the outer join, and `AddedAt` is a `string` because SQLite has no `datetimeoffset`:

```csharp
private sealed class Row
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public int? ItemId { get; set; }
    public int? QuoteId { get; set; }
    public string? Author { get; set; }
    public string? Text { get; set; }
    public string? AddedAt { get; set; }
}
```

## Timing

200 collections × 25 items (5,000 quotes, 5,000 membership rows), 500 iterations after a 50-call
warm-up, Release build, one shared SQLite connection. Three runs:

| | µs per call | |
|---|---|---|
| EF | 2716 – 2860 | |
| Dapper | 140 – 157 | 17.9× – 20.2× faster |
| EF's SQL, run through Dapper | 2143 – 2280 | |

That third row is the interesting one. Feeding EF's own generated SQL to Dapper's mapper still costs
~2.2 ms, so **about three quarters of the gap is the SQL EF generated, not the mapper**. EF's own
pipeline accounts for the remaining ~600 µs.

## The SQL, and why the shapes differ

Dapper's, driven from the one collection row:

```sql
SELECT  c.Id, c.Name, c.OwnerId,
        i.Id AS ItemId, q.Id AS QuoteId, q.Author, q.Text, i.AddedAt
FROM Collections AS c
LEFT JOIN CollectionItems AS i ON i.CollectionId = c.Id
LEFT JOIN Quotes AS q ON q.Id = i.QuoteId AND q.IsDeleted = 0
WHERE c.Id = @id
ORDER BY i.Id
```

```
SEARCH c USING INTEGER PRIMARY KEY (rowid=?)
SEARCH i USING INDEX IX_CollectionItems_CollectionId (CollectionId=?) LEFT-JOIN
SEARCH q USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN
```

Three index seeks, no scan. EF's version (the SQL is in [piece 1](../day12-read-models/)) plans as:

```
CO-ROUTINE c2
SEARCH c USING INTEGER PRIMARY KEY (rowid=?)
CORRELATED SCALAR SUBQUERY 1
SEARCH c0 USING COVERING INDEX IX_CollectionItems_CollectionId (CollectionId=?)
MATERIALIZE s
SCAN c1
SEARCH q USING INTEGER PRIMARY KEY (rowid=?)
SCAN c2
SCAN s LEFT-JOIN
USE TEMP B-TREE FOR ORDER BY
```

EF puts the items in a derived table that carries no `CollectionId` predicate, so SQLite
materialises **every** membership row joined to its quote — all 5,000 — and sorts them in a temp
B-tree, then joins that to the single collection row. The cost grows with the size of the table
rather than with the size of the collection being read.

## EF can be fixed too

Adding `AsSplitQuery()` to the piece-1 LINQ measured **502 µs per call**, a 5.6× improvement on EF
as-is, because each of the two statements then carries its own filter. Measured the same way but
with a throwaway variant, so that number is not reproducible from this repository.

It is not applied to the shipped query: piece 1's whole point was one round trip, and split mode
trades that for two. Dapper is still ~3.5× faster than the best EF variant here.

## The rule

EF stays the default — it owns the write path, migrations, and one model the team shares, and for
most reads its SQL is fine. Reach for Dapper on a read path only when you have measured it, the
path is genuinely hot, and the cost turns out to be the *shape* EF generated rather than your LINQ:
the giveaway here was that EF's own SQL still took 2.2 ms through Dapper's mapper, so swapping
mappers alone would have bought ~600 µs of the 2.7 ms. Try to fix the shape in EF first, because a
`AsSplitQuery()` or a restructured projection keeps one model and one set of conventions. If the
shape you need is one EF will not generate, or you are hand-tuning against a query plan, write the
SQL and let Dapper map it — and accept that you have taken ownership of the column mapping, the
null handling on outer joins, and the type conversions EF was doing for you.

## Notes

- The Dapper query is **not** wired into the API. The exercise asks for the comparison, and
  `GET /api/collections/{id}` still runs the EF query, so piece 1's evidence stands.
- Writing the SQL by hand meant handling what EF hid: SQLite has no `datetimeoffset`, so `AddedAt`
  comes back as the TEXT EF wrote and is parsed in the mapper, and the outer join makes every item
  column nullable, so `ItemCount` counts rows with an item while `Items` keeps only rows with a
  quote.
- SQLite only. On SQL Server the planner may push the predicate into EF's derived table, which
  would shrink the gap.
- The absolute numbers are from one machine; the ratio and the attribution are点
  the findings, not the microseconds.

## Run it

```bash
dotnet test QuotesApi.Tests/QuotesApi.Tests.csproj -c Release --filter "FullyQualifiedName~DapperVsEfTests" --logger "console;verbosity=detailed"
```
