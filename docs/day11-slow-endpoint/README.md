# Day 11 — Profile a slow endpoint

`GET /api/quotes/by-author` in [`QuoteController.cs`](../../QuotesApi/Controllers/QuoteController.cs)
is deliberately naive: it reads the distinct authors, then loops and reads each author's quotes
one author at a time. `Quote.Author` has no index.

Data: 5,000 quotes across 200 authors, from [`slow_endpoint.sql`](slow_endpoint.sql).
Every number below comes from the run captured in [`results/`](results/).

## Baseline under load

10 connections, 30 seconds, bombardier v1.2.6. Release build, EF command logging off.

| | `/api/quotes/by-author` | `/api/quotes?size=25` (control) |
|---|---|---|
| p50 | 178.65 ms | 1.69 ms |
| p99 | 598.81 ms | 5.32 ms |
| Requests/sec | 43.00 | 5,535.75 |

1,233 requests, all 200. The control is the Day-5 list endpoint on the same host against the
same 5,000 rows, so the difference is the query pattern, not the machine.

## The offending SQL

201 `Executed DbCommand` entries in one request. One author list:

```sql
SELECT DISTINCT "q"."Author"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
```

then the same statement 200 more times, once per author:

```sql
SELECT "q"."Id", "q"."Author", "q"."IsDeleted", "q"."OwnerId", "q"."Text"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted") AND "q"."Author" = @author
```

## The plan

```
-- author list
3|0|0|SCAN TABLE Quotes AS q
13|0|0|USE TEMP B-TREE FOR DISTINCT

-- per-author read
2|0|0|SCAN TABLE Quotes AS q
```

`SCAN TABLE`, not `SEARCH`, because `Quotes` carries no index but its integer primary key.

## The two biggest problems

1. **N+1.** 201 round trips to serve one response. The count follows the number of authors, so
   adding authors adds queries even when the payload stays the same size.
2. **No index on `Quotes.Author`.** Each of those 200 queries scans all 5,000 rows, so a request
   reads about a million rows to return 5,000. The `DISTINCT` also has to build a temp B-tree.

Also visible in the SQL: all five columns come back per row when only `Text` is serialised.

## Run it

```bash
env "ConnectionStrings__DefaultConnection=Data Source=quotes-day11.db" dotnet run -c Release --project QuotesApi --launch-profile http
```

Seed the file the API just created, then load-test the endpoint:

```bash
python -c "import sqlite3; c=sqlite3.connect('QuotesApi/quotes-day11.db'); c.executescript(open('docs/day11-slow-endpoint/slow_endpoint.sql').read()); c.commit()"
```

```bash
bombardier -c 10 -d 30s -l http://localhost:5104/api/quotes/by-author
```

The two `EXPLAIN QUERY PLAN` statements at the end of `slow_endpoint.sql` print the plans; the
captured ones came from Python's SQLite 3.35.5 rather than the app's build, which changes
nothing here because the table has no index either way.
