# Day 7 — joins and CTEs

Return each author with their quote count and their most-recent quote, in one
statement, using a CTE instead of a correlated subquery. The query is in
[`joins_and_ctes.sql`](joins_and_ctes.sql), the output in [`results/`](results).

Database: `QuotesLab` on SQL Server 2022, started with
[`sql/docker-compose.yml`](../docker-compose.yml).

## The query

```sql
WITH AuthorQuote AS
(
    SELECT
        q.AuthorId,
        q.QuoteText,
        q.CreatedAt,
        AuthorQuoteCount = COUNT(*) OVER (PARTITION BY q.AuthorId),
        Recency          = ROW_NUMBER() OVER (
                               PARTITION BY q.AuthorId
                               ORDER BY     q.CreatedAt DESC, q.QuoteId DESC)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
)
SELECT
    a.AuthorId,
    a.FullName,
    QuoteCount        = COALESCE(aq.AuthorQuoteCount, 0),
    MostRecentQuote   = aq.QuoteText,
    MostRecentQuoteAt = aq.CreatedAt
FROM app.Author AS a
LEFT JOIN AuthorQuote AS aq
       ON aq.AuthorId = a.AuthorId
      AND aq.Recency  = 1
ORDER BY QuoteCount DESC, a.FullName;
```

## How it works

One pass over `app.Quote`. `COUNT(*) OVER` attaches the author's total to every
one of their rows, `ROW_NUMBER()` marks the newest, and the outer query keeps
row 1.

`LEFT JOIN` keeps the three authors with no live quotes — Hypatia, Seneca and
Sun Tzu come back with `QuoteCount` 0 and a NULL quote. `QuoteId DESC` breaks
Ada Lovelace's two quotes that share a `CreatedAt`. `IsDeleted = 0` sits in the
CTE because that is the one place a single filter serves both the count and the
quote.

A CTE fits here because both facts come from one named pass over one filtered
set. The correlated version re-derives that set three times per author, with
nothing keeping the three in agreement.

## Result

First rows of nineteen, from
[`results/joins_and_ctes.txt`](results/joins_and_ctes.txt).

```
AuthorId|FullName|QuoteCount|MostRecentQuote|MostRecentQuoteAt
--------|--------|----------|---------------|-----------------
10|Edsger W. Dijkstra|12|Elegance is what remains once the special cases are understood.|2026-08-16 09:10:00
11|Donald Knuth|9|Analysis without a machine is a hypothesis.|2026-08-14 13:00:00
14|C. A. R. Hoare|7|The average of a good algorithm still has a bad day.|2026-08-15 07:50:00
15|Frederick P. Brooks Jr.|7|Documentation is the part of a design that outlives the designer.|2026-08-12 14:05:00
5|Mark Twain|6|Travel cures certainty faster than argument.|2026-08-09 10:50:00
```
