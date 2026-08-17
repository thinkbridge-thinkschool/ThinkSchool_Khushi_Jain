# Day 7 — window functions

## The exercise

Return, per author, each quote with a running count and the gap in days since
their previous quote, using `LAG`.

Database: `QuotesLab` on SQL Server 2022, the same one built for
[the joins and CTEs piece](../day7-joins-and-ctes/README.md). See
[sql/README.md](../README.md) for how to run it.

## The answer

[`07_window_functions.sql`](07_window_functions.sql), section 1.

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

The `WINDOW` clause is SQL Server 2022 syntax, and it is doing real work rather
than saving keystrokes. `ROW_NUMBER`, `COUNT` and `LAG` must all walk an
author's quotes in the same order, or the running count counts one sequence
while the gap measures a different one — and nothing would report that, because
each `OVER` clause is independently valid. Naming the window once makes the
agreement structural instead of a convention three separate clauses have to
keep. It needs compatibility level 160; section 0 prints it, and the equivalent
with the ordering repeated three times sits in a comment underneath for anything
older.

`INNER JOIN`, not `LEFT`. This is a per-quote report, so an author with no
quotes contributes no rows — the opposite of the joins piece, where the answer
was one row per author and the three silent authors had to survive. Same schema,
same week, opposite correct answer.

`LAG` returns NULL for each author's first quote, so `DaysSincePrevious` is NULL
there. That is the honest answer: there is no previous quote, and a substituted
0 would be indistinguishable from a real gap of zero days — a value this data
actually contains.

## Sample rows

First eleven rows of seventy-nine, verbatim from
[`results/07_window_functions.txt`](results/07_window_functions.txt).

```
Author|QuoteId|CreatedAt|QuoteNumber|RunningCount|PreviousQuoteAt|DaysSincePrevious|Quote
------|-------|---------|-----------|------------|---------------|-----------------|-----
Ada Lovelace|22|2026-07-04 10:00:00|1|1|NULL|NULL|A machine follows the argument, never the in
Ada Lovelace|23|2026-07-04 10:00:00|2|2|2026-07-04 10:00:00|0|Notation is the first optimisation.
Alan Turing|26|2026-05-09 10:35:00|1|1|NULL|NULL|Every decidable question is one that somebod
Alan Turing|27|2026-06-05 16:05:00|2|2|2026-05-09 10:35:00|27|The machine is indifferent to your metaphor
Alan Turing|28|2026-06-27 09:55:00|3|3|2026-06-05 16:05:00|22|A shorter proof is usually a better algorith
Alan Turing|29|2026-07-19 13:40:00|4|4|2026-06-27 09:55:00|22|Imitation is a test, not a compliment.
Alan Turing|30|2026-08-11 07:30:00|5|5|2026-07-19 13:40:00|23|A question worth asking survives a precise p
Aristotle|1|2026-05-04 08:10:00|1|1|NULL|NULL|Excellence is a habit before it is an outcom
Aristotle|2|2026-05-19 14:22:00|2|2|2026-05-04 08:10:00|15|A conclusion is only as sound as the premise
Aristotle|3|2026-06-11 09:05:00|3|3|2026-05-19 14:22:00|23|The mean is not the average; it is the fitti
Aristotle|4|2026-07-22 16:40:00|4|4|2026-06-11 09:05:00|41|Naming a thing well is half of understanding
```

Ada Lovelace is the interesting pair: two quotes at the identical `CreatedAt`,
so the second reports a gap of 0 days rather than NULL, and the running count
still goes 1 then 2 rather than 2 then 2. Which is the reason this file is
longer than the exercise required.

## The frame default is `RANGE`, and it is almost never what you meant

An `OVER` clause with `ORDER BY` and no frame does not default to "every row up
to this one". It defaults to `RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT
ROW`, and `RANGE` is defined over *values*, not positions: the frame includes
every row whose ordering value ties with the current row. Two quotes at the same
instant are peers, so both see the whole peer group.

Section 2, run against Ada Lovelace:

```
Author|QuoteId|CreatedAt|RunningWithRows|RunningWithRange|RunningNoFrame
------|-------|---------|---------------|----------------|--------------
Ada Lovelace|22|2026-07-04 10:00:00|1|2|2
Ada Lovelace|23|2026-07-04 10:00:00|2|2|2
```

The running count reads 2, 2. It never reports 1. No error, no warning, and on
data without a tie all three columns agree perfectly — which is precisely how
this survives review.

Two independent things defend section 1, and it is worth being precise about
which does what, because the distinction is where the latent bug lives. `ROWS`
fixes the frame. `QuoteId` in the `ORDER BY` makes the ordering unique, and a
unique ordering has no peers, so `RANGE` and `ROWS` would agree anyway. Section
1 has both — so if someone later drops the tiebreaker for a plausible-looking
reason the explicit `ROWS` still holds, and if someone drops the frame the
tiebreaker does. Relying on either alone is one edit away from wrong.

The same default catches `LAST_VALUE`, more visibly. With the frame ending at
`CURRENT ROW`, "the last value in the window" is the current row itself:

```
Author|QuoteId|CreatedAt|FirstQuoteAt|LastQuoteWrong|LastQuoteRight
------|-------|---------|------------|--------------|--------------
Marie Curie|24|2026-05-26 08:20:00|2026-05-26 08:20:00|2026-05-26 08:20:00|2026-07-13 14:10:00
Marie Curie|25|2026-07-13 14:10:00|2026-05-26 08:20:00|2026-07-13 14:10:00|2026-07-13 14:10:00
```

`FIRST_VALUE` is correct only by accident — the default frame already starts at
`UNBOUNDED PRECEDING`.

## What else is in the file

**Section 3 — `ROW_NUMBER` vs `RANK` vs `DENSE_RANK`.** Shown twice: on a tied
timestamp, and on a tied aggregate where Hoare and Brooks both have 7 quotes.
`ROW_NUMBER` gives 3 and 4, and which of the two goes first is arbitrary; `RANK`
gives 3 and 3 then skips to 5; `DENSE_RANK` gives 3 and 3 then 4. Which is right
depends on the sentence — "third equal, and nobody is fourth" is `RANK`, "the
third-largest distinct total" is `DENSE_RANK`.

**Section 4 — `LAG` and `LEAD` together**, the ten longest silences between an
author's consecutive quotes, plus offsets (`LAG(x, 2)`) and the optional default
argument. It also shows why `DATEDIFF(DAY, ...)` is not elapsed time: the
longest gap reads 49 days by `DATEDIFF` and 48.84 by exact arithmetic, because
`DATEDIFF` counts date-boundary crossings. The exercise asked for days, so days
is what section 1 returns, but both columns sit side by side here.

**Section 5 — `SUM() OVER (ORDER BY ...)`**, four windows over one author: a
running total (`ROWS UNBOUNDED PRECEDING`), a partition-wide total (no `ORDER
BY` at all, so no frame), the share of the author's output reached so far, and a
three-quote moving average (`ROWS BETWEEN 2 PRECEDING AND CURRENT ROW`). Then a
corpus-wide running total with no `PARTITION BY`, counting across every author
at once.

**Section 6 — window vs `GROUP BY`.** The same aggregate both ways: `GROUP BY`
returns 2 rows and the quotes are gone; the window returns all 4 with the
author's total attached to each. A running count is unambiguously a question
about rows, because it changes on every one of them.

## What would break this

Ties in the ordering — the same failure as the joins piece wearing different
clothes. There it duplicated an author; here it silently flattens a running
count. `CreatedAt` is `datetime2(0)`, so ties are a full second wide and not
rare at API insert rates.

`DATEDIFF(DAY, ...)` counts boundary crossings, so two quotes four minutes apart
across midnight report a one-day gap. Real interval arithmetic wants seconds, or
`DATEDIFF_BIG`, which does not overflow past about 68 years.

The `WINDOW` clause needs compatibility level 160. A database restored from an
older server fails with a syntax error rather than anything self-explanatory,
which is why section 0 prints the level before anything else runs.

At scale, every window here sorts the live table by `(AuthorId, CreatedAt,
QuoteId)` ascending. `IX_Quote_Author_CreatedAt` is keyed `(AuthorId, CreatedAt
DESC, QuoteId DESC)` — descending, because the joins piece wanted the newest row
first. SQL Server can read an index backwards, so it can serve this order
without a sort, but that is the optimiser choosing to rather than something the
index guarantees. If these queries mattered more than the joins ones, the index
direction is the first thing I would revisit.
