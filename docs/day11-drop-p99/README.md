# Day 11 — Drop p99 by 10×

Fixes the two problems found in [day11-slow-endpoint](../day11-slow-endpoint/): the N+1 over
authors and the missing index. Same endpoint, same data, same load.
Numbers below come from the run in [`results/`](results/).

## Before / after

10 connections, 30 seconds, bombardier v1.2.6, Release build, EF command logging off.

| | before | after | change |
|---|---|---|---|
| p99 | 598.81 ms | 20.09 ms | 29.8× faster |
| p50 | 178.65 ms | 11.09 ms | 16.1× faster |
| Requests/sec | 43.00 | 893.06 | 20.8× |
| Queries per request | 201 | 1 | |

The response is byte-identical to the old one (105,894 bytes) and carries the same 200 authors
and 5,000 quotes, so the comparison is like for like. p99 across four runs of the final build was
20.09, 20.98, 21.63 and 31.25 ms — the worst is still 19× better than before.

## The changes

**One query instead of 201.** The loop is gone. The endpoint now projects the two columns it
actually serialises and groups them in memory:

```csharp
var rows = await db.Quotes
    .Where(q => !q.IsDeleted)
    .OrderBy(q => q.Author)
    .Select(q => new { q.Author, q.Text })
    .ToListAsync(cancellationToken);

var byAuthor = rows
    .GroupBy(row => row.Author)
    .Select(group => new { author = group.Key, quotes = group.Select(row => row.Text) });
```

`AsNoTracking()` is no longer needed — a projection to an anonymous type was never tracked.

**An index on `(Author, IsDeleted)`**, in `QuotesDbContext.OnModelCreating` and the
`AddQuoteAuthorIndex` migration. `Author` leads so the index also satisfies the `ORDER BY`;
`IsDeleted` lets the soft-delete filter be answered from the index. `Text` is not in the key:
SQL Server cannot key an index on `nvarchar(max)`, and this model is shared with the SQL Server
migration set the Testcontainers suite uses.

## Before / after plans

```
-- before, per-author read, run 200 times
2|0|0|SCAN TABLE Quotes AS q

-- before, author list, run once
3|0|0|SCAN TABLE Quotes AS q
13|0|0|USE TEMP B-TREE FOR DISTINCT

-- after, the only query
4|0|0|SCAN TABLE Quotes AS q USING INDEX IX_Quotes_Author_IsDeleted
```

Both `SCAN TABLE` lines are gone, and so is the temp B-tree: the index supplies the row order
and answers the filter.

## What the index was actually worth

Measured on the same endpoint code before the index existed: p50 12.69 ms, p99 28.75 ms. So
eliminating the N+1 accounts for nearly all of the gain, and at 5,000 rows the index's effect on
latency sits inside run-to-run variance. It still changes the plan, and it is the index the old
per-author query needed, so it earns its place — but the honest attribution is that query count,
not the index, was the problem.

Two other candidates were measured and rejected: `(Author)` alone (p99 21.34 ms, keeps a table
lookup per row) and `(Author, IsDeleted, Text)` (p99 20.59 ms, fully covering, but not portable
to SQL Server).

## Run it

Same steps as piece 1 — seed with
[`../day11-slow-endpoint/slow_endpoint.sql`](../day11-slow-endpoint/slow_endpoint.sql), then:

```bash
bombardier -c 10 -d 30s -l http://localhost:5104/api/quotes/by-author
```
