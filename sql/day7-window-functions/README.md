# Day 7 — window functions

Return, per author, each quote with a running count and the gap in days since
their previous quote, using `LAG`. The queries are in
[`07_window_functions.sql`](07_window_functions.sql), the output in
[`results/`](results).

Database: `QuotesLab` on SQL Server 2022, started with
[`sql/docker-compose.yml`](../docker-compose.yml).

## The query

Section 1 of the file.

```sql
SELECT
    Author            = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    QuoteNumber       = ROW_NUMBER() OVER w,
    RunningCount      = COUNT(*) OVER (w ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
    PreviousQuoteAt   = LAG(q.CreatedAt) OVER w,
    DaysSincePrevious = DATEDIFF(DAY, LAG(q.CreatedAt) OVER w, q.CreatedAt),
    Quote             = LEFT(q.QuoteText, 44)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
WINDOW w AS (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId)
ORDER BY a.FullName, q.CreatedAt, q.QuoteId;
```

`ROW_NUMBER`, `COUNT` and `LAG` all have to walk an author's quotes in the same
order, or the running count counts one sequence while the gap measures another.
The `WINDOW` clause names that order once so all three share it. It needs
compatibility level 160; section 0 prints the level, and the older equivalent
sits in a comment underneath.

`INNER JOIN`, not `LEFT` — this is a per-quote report, so an author with no
quotes contributes no rows. `LAG` returns NULL for each author's first quote, so
`DaysSincePrevious` is NULL there rather than 0, which would look like a real
same-day gap.

## Sample rows

First rows of seventy-nine, from
[`results/07_window_functions.txt`](results/07_window_functions.txt).

```
Author|QuoteId|CreatedAt|QuoteNumber|RunningCount|PreviousQuoteAt|DaysSincePrevious|Quote
------|-------|---------|-----------|------------|---------------|-----------------|-----
Ada Lovelace|22|2026-07-04 10:00:00|1|1|NULL|NULL|A machine follows the argument, never the in
Ada Lovelace|23|2026-07-04 10:00:00|2|2|2026-07-04 10:00:00|0|Notation is the first optimisation.
Alan Turing|26|2026-05-09 10:35:00|1|1|NULL|NULL|Every decidable question is one that somebod
Alan Turing|27|2026-06-05 16:05:00|2|2|2026-05-09 10:35:00|27|The machine is indifferent to your metaphor
Alan Turing|28|2026-06-27 09:55:00|3|3|2026-06-05 16:05:00|22|A shorter proof is usually a better algorith
```

Ada Lovelace has two quotes at the same `CreatedAt`, so the second reports a gap
of 0 days and the running count still goes 1 then 2.

## The frame default is `RANGE`

An `OVER` clause with `ORDER BY` and no frame defaults to `RANGE BETWEEN
UNBOUNDED PRECEDING AND CURRENT ROW`. `RANGE` is defined over values, not
positions, so rows that tie on the ordering value all see the whole tied group.
Section 2, on Ada Lovelace:

```
Author|QuoteId|CreatedAt|RunningWithRows|RunningWithRange|RunningNoFrame
------|-------|---------|---------------|----------------|--------------
Ada Lovelace|22|2026-07-04 10:00:00|1|2|2
Ada Lovelace|23|2026-07-04 10:00:00|2|2|2
```

The running count reads 2, 2 — no error, and on data without a tie all three
columns agree. Section 1 is protected twice over: `ROWS` fixes the frame, and
`QuoteId` in the `ORDER BY` makes the ordering unique so there are no ties to
begin with. The same default catches `LAST_VALUE`, where "the last value in the
window" ends up being the current row.

## The rest of the file

- **Section 3** — `ROW_NUMBER` vs `RANK` vs `DENSE_RANK` on a tied timestamp and
  a tied count. On two authors with 7 quotes each: `ROW_NUMBER` gives 3 and 4,
  `RANK` gives 3, 3, then 5, `DENSE_RANK` gives 3, 3, then 4.
- **Section 4** — `LAG` and `LEAD` together, the longest gaps between quotes,
  plus offsets and the default argument. Also shows `DATEDIFF(DAY, ...)`
  counting date boundaries, not elapsed time.
- **Section 5** — `SUM() OVER`: running total, partition total, running share,
  and a three-quote moving average.
- **Section 6** — window vs `GROUP BY`. `GROUP BY` collapses to 2 rows and loses
  the quotes; the window keeps all 4 with the total attached.

## What would break this

Ties in the ordering. `CreatedAt` is `datetime2(0)`, so ties are a full second
wide and not rare at insert rates.

`DATEDIFF(DAY, ...)` counts boundary crossings, so two quotes four minutes apart
across midnight report a one-day gap.

The `WINDOW` clause needs compatibility level 160. An older database fails with
a plain syntax error, which is why section 0 prints the level first.

Every window here sorts ascending by `(AuthorId, CreatedAt, QuoteId)`, while
`IX_Quote_Author_CreatedAt` is keyed descending for the joins piece. SQL Server
can read the index backwards, but that is the optimiser's choice, not a
guarantee.
