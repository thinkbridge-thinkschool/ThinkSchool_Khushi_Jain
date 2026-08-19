# Day 7 — joins and CTEs

## The exercise

Return each author with their quote count and their most-recent quote, in one
statement, using a CTE rather than a correlated subquery in the SELECT.

Database: `QuotesLab` on SQL Server 2022. See [sql/README.md](../README.md) for
how to run it.

## The answer

[`joins_and_ctes.sql`](joins_and_ctes.sql)

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

One pass over `app.Quote`: `COUNT(*) OVER` attaches the author's total to every
one of their rows, `ROW_NUMBER()` marks the newest, and the outer query keeps
row 1.

`LEFT JOIN` keeps the three authors with no live quotes. `QuoteId DESC` breaks
Ada Lovelace's two quotes sharing a `CreatedAt`. `IsDeleted = 0` sits in the CTE
because that is the only place one filter serves both the count and the quote.

## Result set

First ten of nineteen rows, from
[`results/joins_and_ctes.txt`](results/joins_and_ctes.txt).

```
AuthorId|FullName|QuoteCount|MostRecentQuote|MostRecentQuoteAt
--------|--------|----------|---------------|-----------------
10|Edsger W. Dijkstra|12|Elegance is what remains once the special cases are understood.|2026-08-16 09:10:00
11|Donald Knuth|9|Analysis without a machine is a hypothesis.|2026-08-14 13:00:00
14|C. A. R. Hoare|7|The average of a good algorithm still has a bad day.|2026-08-15 07:50:00
15|Frederick P. Brooks Jr.|7|Documentation is the part of a design that outlives the designer.|2026-08-12 14:05:00
5|Mark Twain|6|Travel cures certainty faster than argument.|2026-08-09 10:50:00
8|Alan Turing|5|A question worth asking survives a precise phrasing.|2026-08-11 07:30:00
12|Barbara Liskov|5|The interface is the contract; the code is only evidence.|2026-08-08 08:05:00
13|Leslie Lamport|5|Consensus is expensive because disagreement is cheap.|2026-08-10 16:35:00
1|Aristotle|4|Naming a thing well is half of understanding it.|2026-07-22 16:40:00
9|Grace Hopper|4|A nanosecond is a length before it is a duration.|2026-08-13 15:20:00
```

Hypatia, Seneca and Sun Tzu come back last with `QuoteCount` 0 and a NULL quote.

## Why a CTE here over a correlated subquery

Because both facts fall out of one named pass over one filtered set, where the
correlated version re-derives that set three times per author with nothing
forcing the three to stay in agreement.
