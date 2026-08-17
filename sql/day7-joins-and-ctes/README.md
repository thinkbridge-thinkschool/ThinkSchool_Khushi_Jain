# Day 7 — joins and CTEs at depth

## The exercise

Return each author with their quote count and their most-recent quote, in one
statement, using a CTE rather than a correlated subquery in the SELECT.

Database: `QuotesLab` on SQL Server 2022. See [sql/README.md](../README.md) for
why a fresh schema rather than the API's own, and how to run it.

## The answer

[`04_author_quote_summary.sql`](04_author_quote_summary.sql), section 1.

```sql
WITH AuthorQuote AS
(
    SELECT
        q.AuthorId,
        q.QuoteId,
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

One pass over `app.Quote`. `COUNT(*) OVER (PARTITION BY AuthorId)` attaches the
author's total to every one of that author's rows, `ROW_NUMBER()` over the same
partition marks the newest, and the outer query keeps only row 1 — so the count
arrives already paired with the quote it belongs to.

Three details are load-bearing rather than decorative.

`LEFT JOIN`, not inner. Hypatia, Seneca and Sun Tzu have no live quotes, and the
question asked for each author, not each author who has written. They return
with `QuoteCount` 0 and a NULL quote.

`QuoteId DESC` as the second `ORDER BY` term. Ada Lovelace has two quotes at
exactly the same `CreatedAt`. Ordering on the timestamp alone leaves
`ROW_NUMBER()` free to pick either, and free to pick differently on the next run
or under a different plan. The tiebreaker makes the answer a fact rather than a
coincidence.

`IsDeleted = 0` inside the CTE, which is the only place it can go. Mark Twain's
most recent quote by date is soft-deleted; a query that omits the filter returns
a different quote for him and reports no problem.

## Result set

First ten of nineteen rows, copied verbatim from
[`results/04_author_quote_summary.txt`](results/04_author_quote_summary.txt).
Nothing in this block is typed by hand — the seed is deterministic, so the file
the runner produces is the evidence.

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

The rows that prove the most are below the cut. Hypatia, Seneca and Sun Tzu
come back last with `QuoteCount` 0 and a NULL quote, which is the `LEFT JOIN`
earning its place. Ada Lovelace returns `Notation is the first optimisation.` —
`QuoteId` 23 rather than 22, both stamped `2026-07-04 10:00:00`, decided by the
tiebreaker rather than by luck.

Section 5 of the query file compares the CTE version against the correlated
version with `EXCEPT` in both directions and reports `0|0`, so the rewrite is
demonstrably behaviour-preserving. `EXCEPT` treats NULLs as equal, so the three
authors with no quotes are genuinely checked rather than quietly skipped.

## Why a CTE here over a correlated subquery

Because the count and the latest quote fall out of one named pass over one
filtered set, where the correlated version re-derives that same set three times
per author and leaves nothing forcing the three to agree when one of them is
later edited.

Both halves of that turn out to be true, and I had expected only the second one
to be. The maintenance argument is the durable one: nothing stops a later edit
adding `AND q.CategoryId IS NOT NULL` to the count subquery and not to the other
two, producing a row whose count and whose quote describe different populations
— a bug with no symptom except a wrong number. The CTE has one `WHERE` clause,
so that class of bug has nowhere to live.

I had assumed the runtime argument was weak, on the theory that the optimiser
would collapse the two identical `TOP (1)` lookups. It does not.
[`06_plans.sql`](06_plans.sql) shows the correlated plan executing them as two
separate `Top(1)` plus `Index Seek` pairs per author, alongside a third
correlated `Clustered Index Scan` for the count. The window version reads the
index once.

## What else is here

[`03_joins.sql`](03_joins.sql) — inner, left, anti and cross joins, each paired
with the mistake it is usually the victim of. The theme is that the three common
ways of losing rows are all silent: an inner join over a nullable foreign key, a
right-table predicate stranded in `WHERE`, and a `COUNT(*)` taken after a
one-to-many join. None raises an error; all three change the number. Section 3.7
uses a cross join for what it is actually good at — an author-by-tag coverage
grid, which can report a combination that has never occurred, something no
aggregate over the link table can do.

[`05_recursive_cte.sql`](05_recursive_cte.sql) — the `app.Category` tree with
depth and materialised path, a descendant roll-up that answers "how many quotes
sit under Science" including everything beneath it, upward recursion for
breadcrumbs, and a cycle demonstration on a temp copy of the tree showing why
`MAXRECURSION` is a backstop and a visited-path guard is the actual fix. Section
5 is deliberately non-recursive: most CTEs in real code are named stages, not
traversals.

[`06_plans.sql`](06_plans.sql) — estimated plans for the three shapes, so the
performance claims above are evidence rather than assertion. It returns no rows;
`SET SHOWPLAN_TEXT ON` produces plans instead of results.

The seed plants the edge cases these queries hunt for — authors with no quotes,
an author whose only quote is soft-deleted, quotes with a NULL category, a tied
timestamp, and a tag used by only four authors. See the header of
[`02_seed.sql`](../schema/02_seed.sql).

## What the plans showed

Two things worth recording, both from
[`results/06_plans.txt`](results/06_plans.txt).

The window version reads `IX_Quote_Author_CreatedAt` as an `Index Scan` marked
`ORDERED FORWARD`, feeding `Segment` and `Sequence Project` with **no `Sort`
between them**. The index key is `(AuthorId, CreatedAt DESC, QuoteId DESC)`,
which is exactly the window's `PARTITION BY` plus `ORDER BY`, so the ranking is
free. That is why `QuoteId DESC` sits in the index key rather than the `INCLUDE`
list: a tiebreaker outside the key order is equally correct and reintroduces the
sort. The single `Sort` at the top of the plan is the final
`ORDER BY QuoteCount DESC`, which cannot be indexed away because the count does
not exist until the window is computed.

The `OUTER APPLY` variant is half as good as I claimed before reading its plan.
Its `APPLY` half is excellent — `Index Seek` on the same index, `ORDERED
FORWARD`, under a `Top(1)`, one row read per author regardless of how prolific
that author is. But `QuoteStats` beside it is referenced only once, so the
optimiser inlines rather than spools it, and the count becomes a
`Clustered Index Scan` of `PK_Quote` with a residual predicate driven by nested
loops: one scan of the whole table per author. Making `APPLY` genuinely win at
scale means sourcing the count from something that is not a per-author scan — an
indexed view, a maintained counter column, or a pre-aggregated pass materialised
before the join.

## What would break this

The window function is computed over every live quote before the outer query
discards all but one row per author. At eighty rows that is free. At ten million
the spool is over the whole table to return one row per author, and the window
version stops being obviously right — but as the plans above show, the naive
`APPLY` rewrite is not the fix, because its aggregate half degrades faster than
its ranking half improves.

`COUNT(*) OVER (PARTITION BY ...)` and `ROW_NUMBER() OVER (PARTITION BY ...)`
share a partition here, which is the only reason one pass suffices. Change
either the filter or the partition on one of them — count every quote including
deleted ones, but show the newest live one — and the single CTE cannot express
it, because one `WHERE` clause has to serve both. That is what section 4a is
for.

`CreatedAt` is `datetime2(0)`, so ties are one second wide. At API insert rates
that is not rare, which is the practical reason the `QuoteId` tiebreaker exists
rather than a theoretical one. Ada Lovelace is that case, planted deliberately.

The filtered index carries `WHERE IsDeleted = 0`, and a connection with
`QUOTED_IDENTIFIER` off cannot use it — the plan silently falls back to a
clustered scan with no error anywhere. Every script sets the option explicitly
and the runner passes `-I` rather than trusting the client default.

Finally, this is not the API's database. Nothing keeps the two schemas in step,
and if `QuotesApi` ever gains a real `Author` table these scripts will not learn
about it.
