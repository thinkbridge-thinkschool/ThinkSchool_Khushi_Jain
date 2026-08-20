# Day 10 — Query translation and projections

[`QueryTranslationTests.cs`](QueryTranslationTests.cs) captures the SQL EF Core actually
sends, via `LogTo(RelationalEventId.CommandExecuted)` and `EnableSensitiveDataLogging()`.
Every block below is copied from a test run.

The query is the same shape as the list read in
[`QuoteRepository.GetPagedAsync`](../QuotesApi/Repositories/QuoteRepository.cs).

## Original — whole entities

```csharp
db.Quotes.Where(q => !q.IsDeleted).OrderBy(q => q.Id).AsNoTracking()
```

```
Executed DbCommand (2ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "q"."Id", "q"."Author", "q"."IsDeleted", "q"."OwnerId", "q"."Text"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
ORDER BY "q"."Id"
```

All five mapped columns, including `Text`, which is the widest one and the one a list
view never shows in full.

## Projected — only what the DTO needs

```csharp
db.Quotes.Where(q => !q.IsDeleted).OrderBy(q => q.Id).AsNoTracking()
    .Select(q => new QuoteSummary(q.Id, q.Author))
```

```
Executed DbCommand (0ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "q"."Id", "q"."Author"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
ORDER BY "q"."Id"
```

Five columns down to two. `IsDeleted` still appears in the `WHERE` clause — filtering on a
column does not mean fetching it.

## The client evaluation

A case-insensitive author match, written the way it would be outside a query:

```csharp
db.Quotes.Where(q => q.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
```

EF Core cannot translate it and refuses to run, rather than silently pulling the table
into memory:

```
The LINQ expression 'DbSet<Quote>()
    .Where(q => q.Author.Equals(
        value: @author,
        comparisonType: OrdinalIgnoreCase))' could not be translated. Additional
information: Translation of the 'string.Equals' overload with a 'StringComparison'
parameter is not supported. Either rewrite the query in a form that can be translated,
or switch to client evaluation explicitly by inserting a call to 'AsEnumerable',
'AsAsyncEnumerable', 'ToList', or 'ToListAsync'.
```

Fixed by lowering the column instead of asking .NET to compare:

```csharp
db.Quotes.Where(q => q.Author.ToLower() == author)
```

```
Executed DbCommand (0ms) [Parameters=[@author='ada lovelace' (Size = 12)], CommandType='Text', CommandTimeout='30']
SELECT "q"."Id", "q"."Author", "q"."IsDeleted", "q"."OwnerId", "q"."Text"
FROM "Quotes" AS "q"
WHERE lower("q"."Author") = @author
```

The comparison now runs in the database. `@author='ada lovelace'` is visible only because
`EnableSensitiveDataLogging` is on — without it the value reads `?`.

## Logging in the app

`appsettings.Development.json` already logs `Microsoft.EntityFrameworkCore.Database.Command`
at `Debug`. `AddPersistence` adds `EnableSensitiveDataLogging()` in Development only, so
parameter values appear locally and never in a deployed log.

## Run it

```bash
dotnet test QuotesApi.Tests/QuotesApi.Tests.csproj --filter "FullyQualifiedName~QueryTranslationTests" -c Release --logger "console;verbosity=detailed"
```
