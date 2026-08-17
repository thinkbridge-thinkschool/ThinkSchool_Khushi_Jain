/* ============================================================================
   Day 7 — window functions
   ----------------------------------------------------------------------------
   "Return, per author, each quote with a running count and the gap in days
    since their previous quote."

   Section 1 is the answer. Sections 2 to 6 are the reasoning: the frame default
   that quietly breaks running totals, the three ranking functions and what they
   do to ties, LAG and LEAD in both directions, SUM() OVER as a running total,
   and the one-line difference between a window and a GROUP BY.

   The whole point of a window function is that it does not collapse the row.
   GROUP BY answers "how many quotes does this author have" and destroys the
   quotes doing it. A window answers the same question and still hands back
   every quote, which is the only reason a running count is expressible at all.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* The WINDOW clause used in section 1 is SQL Server 2022 syntax and needs
   database compatibility level 160. A database created on 2022 gets it by
   default; one restored from an older server does not, and the failure is a
   syntax error rather than anything self-explanatory. */
PRINT '=== 0.  Compatibility level (WINDOW clause needs 160) ===';

SELECT
    DatabaseName       = name,
    CompatibilityLevel = compatibility_level
FROM sys.databases
WHERE name = N'QuotesLab';
GO

/* ============================================================================
   1.  THE ANSWER — every quote, its running count, and the gap since the last.
   ----------------------------------------------------------------------------
   One statement, one window definition, three functions reading from it.

   The WINDOW clause is doing real work here rather than saving keystrokes.
   ROW_NUMBER, COUNT and LAG must all walk the author's quotes in the same
   order, or the running count counts one sequence while the gap measures a
   different one -- and nothing would report that, because each OVER clause is
   independently valid. Naming the window once makes the agreement structural.

   Ordering is ascending: oldest quote first. Task 1 ordered descending because
   it wanted the newest row; a running total has to start at the beginning.

   QuoteId is in the ORDER BY as a tiebreaker for the same reason as task 1 --
   Ada Lovelace has two quotes at an identical CreatedAt, and without it the
   sequence numbering is decided by whatever the plan happens to do.

   ROWS, not the default. See section 2; this is the line most running totals
   get wrong.

   INNER JOIN, not LEFT. This is a per-quote report, so an author with no
   quotes contributes no rows -- the opposite of task 1, where the answer was
   one row per author and the three silent authors had to survive.

   LAG returns NULL for each author's first quote, so DaysSincePrevious is NULL
   there. That is the honest answer: there is no previous quote, and 0 would
   claim the gap was measured and came out empty.
   ============================================================================ */
PRINT '=== 1.  Per author, each quote, running count, days since previous ===';

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
GO

/* For a database below compatibility level 160, the same query without the
   WINDOW clause. Identical result, three chances to mistype the ordering:

   SELECT
       Author            = a.FullName,
       q.QuoteId,
       q.CreatedAt,
       QuoteNumber       = ROW_NUMBER() OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId),
       RunningCount      = COUNT(*)     OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId
                                              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
       PreviousQuoteAt   = LAG(q.CreatedAt) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId),
       DaysSincePrevious = DATEDIFF(DAY,
                               LAG(q.CreatedAt) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId),
                               q.CreatedAt)
   FROM app.Quote AS q
   INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
   WHERE q.IsDeleted = 0
   ORDER BY a.FullName, q.CreatedAt, q.QuoteId;
*/

/* ============================================================================
   2.  The frame default, which is RANGE and is almost never what you meant.
   ----------------------------------------------------------------------------
   An OVER clause with ORDER BY and no frame does not default to "every row up
   to this one". It defaults to

       RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW

   and RANGE is defined in terms of *values*, not positions: the frame includes
   every row whose ORDER BY value ties with the current row. Two quotes at the
   same instant are peers, so both see a running count of 2 and neither ever
   reports 1. The sequence 1, 2 becomes 2, 2.

   ROWS counts positions and gives the answer intended.

   Note the ORDER BY below deliberately omits QuoteId. Adding it breaks the tie
   and hides the whole effect, which is exactly how this bug survives review:
   it only appears when two rows genuinely tie, and a demo with distinct
   timestamps shows RANGE and ROWS agreeing perfectly.

   RANGE is also the slower of the two, because peer groups have to be
   materialised before the aggregate can be produced.
   ============================================================================ */
PRINT '=== 2.  ROWS vs RANGE on a tied timestamp (Ada Lovelace) ===';

SELECT
    Author            = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    RunningWithRows   = COUNT(*) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt
                                       ROWS  BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
    RunningWithRange  = COUNT(*) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt
                                       RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
    RunningNoFrame    = COUNT(*) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt),
    Quote             = LEFT(q.QuoteText, 44)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName  = N'Ada Lovelace'
ORDER BY q.CreatedAt, q.QuoteId;
GO

/* The same default catches LAST_VALUE, and more visibly. With the implicit
   RANGE frame ending at CURRENT ROW, "the last value in the window" is the
   current row itself, so LAST_VALUE returns the row it was called on and looks
   broken. It needs the frame opened to the end of the partition to mean what
   its name says. FIRST_VALUE happens to be right by accident, because the
   frame already starts at UNBOUNDED PRECEDING. */
PRINT '--- LAST_VALUE with the default frame vs an explicit one ---';

SELECT
    Author         = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    FirstQuoteAt   = FIRST_VALUE(q.CreatedAt) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId),
    LastQuoteWrong = LAST_VALUE(q.CreatedAt)  OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId),
    LastQuoteRight = LAST_VALUE(q.CreatedAt)  OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId
                                                   ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName  = N'Marie Curie'
ORDER BY q.CreatedAt, q.QuoteId;
GO

/* ============================================================================
   3.  ROW_NUMBER, RANK, DENSE_RANK — three answers to one question about ties.
   ----------------------------------------------------------------------------
   ROW_NUMBER is total: every row gets a distinct number, and which of two tied
   rows goes first is arbitrary unless the ORDER BY breaks the tie itself. That
   arbitrariness is a feature when you want exactly one row per group and do not
   care which, and a bug the moment the answer has to be stable across runs.

   RANK gives tied rows the same number and then skips, so two rows at rank 3
   are followed by rank 5. DENSE_RANK gives the same number and does not skip,
   so the next distinct value is 4.

   Which is right depends on the sentence you are trying to write. "Third
   equal, and nobody is fourth" is RANK. "The third-largest distinct total" is
   DENSE_RANK.
   ============================================================================ */
PRINT '=== 3a.  The three on a tied timestamp ===';

SELECT
    Author       = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    RowNumber    = ROW_NUMBER() OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt),
    RankPos      = RANK()       OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt),
    DenseRankPos = DENSE_RANK() OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName  = N'Ada Lovelace'
ORDER BY q.CreatedAt, q.QuoteId;
GO

PRINT '=== 3b.  The three on a tied aggregate (Hoare and Brooks both have 7) ===';

WITH AuthorTotals AS
(
    SELECT
        q.AuthorId,
        QuoteCount = COUNT(*)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
)
SELECT TOP (10)
    Author       = a.FullName,
    t.QuoteCount,
    RowNumber    = ROW_NUMBER() OVER (ORDER BY t.QuoteCount DESC, a.FullName),
    RankPos      = RANK()       OVER (ORDER BY t.QuoteCount DESC),
    DenseRankPos = DENSE_RANK() OVER (ORDER BY t.QuoteCount DESC)
FROM AuthorTotals AS t
INNER JOIN app.Author AS a ON a.AuthorId = t.AuthorId
ORDER BY t.QuoteCount DESC, a.FullName;
GO

/* ============================================================================
   4.  LAG and LEAD — the row behind and the row ahead.
   ----------------------------------------------------------------------------
   Both take an optional offset and an optional default. LAG(x, 2) reads two
   rows back; LAG(x, 1, 0) substitutes 0 where there is no previous row. The
   default argument is worth knowing about and usually worth declining: a
   fabricated 0 for "no previous quote" is indistinguishable from a real gap of
   zero days, which is a value this data actually contains.

   DATEDIFF(DAY, ...) counts boundary crossings, not elapsed time. Two quotes
   23:59 and 00:01 apart are one minute apart and DATEDIFF reports 1 day. The
   exercise asked for days, so days is what section 1 returns, but the exact
   figure is below for comparison. DATEDIFF(SECOND, ...) overflows past about
   68 years; DATEDIFF_BIG is the version that does not.
   ============================================================================ */
PRINT '=== 4.  The ten longest silences between consecutive quotes ===';

WITH Gaps AS
(
    SELECT
        q.AuthorId,
        q.QuoteId,
        q.CreatedAt,
        q.QuoteText,
        PreviousAt     = LAG(q.CreatedAt)  OVER w,
        PreviousText   = LAG(q.QuoteText)  OVER w,
        NextAt         = LEAD(q.CreatedAt) OVER w
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    WINDOW w AS (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId)
)
SELECT TOP (10)
    Author        = a.FullName,
    g.QuoteId,
    GapDays       = DATEDIFF(DAY, g.PreviousAt, g.CreatedAt),
    GapDaysExact  = CAST(DATEDIFF(SECOND, g.PreviousAt, g.CreatedAt) / 86400.0 AS decimal(10, 2)),
    DaysToNext    = DATEDIFF(DAY, g.CreatedAt, g.NextAt),
    CameAfter     = LEFT(g.PreviousText, 38),
    ThisQuote     = LEFT(g.QuoteText, 38)
FROM Gaps AS g
INNER JOIN app.Author AS a ON a.AuthorId = g.AuthorId
WHERE g.PreviousAt IS NOT NULL
ORDER BY GapDays DESC, a.FullName, g.QuoteId;
GO

PRINT '--- offsets and defaults: one back, two back, and a substituted default ---';

SELECT
    Author        = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    OneBack       = LAG(q.CreatedAt, 1) OVER w,
    TwoBack       = LAG(q.CreatedAt, 2) OVER w,
    GapOrNull     = DATEDIFF(DAY, LAG(q.CreatedAt, 1) OVER w, q.CreatedAt),
    GapWithZero   = DATEDIFF(DAY, LAG(q.CreatedAt, 1, q.CreatedAt) OVER w, q.CreatedAt)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName  = N'Grace Hopper'
WINDOW w AS (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId)
ORDER BY q.CreatedAt, q.QuoteId;
GO

/* ============================================================================
   5.  SUM() OVER (ORDER BY ...) — running totals, and frames that move.
   ----------------------------------------------------------------------------
   Four windows over one author, each answering a different question:

     RunningChars    cumulative, partition-scoped   ROWS UNBOUNDED PRECEDING
     AuthorTotal     the whole partition            no ORDER BY at all
     ShareSoFar      the first divided by the second
     Rolling3Avg     a frame that moves             ROWS BETWEEN 2 PRECEDING AND CURRENT ROW
     CorpusRunning   cumulative across every author no PARTITION BY

   An OVER clause with no ORDER BY has no frame and covers the entire
   partition. That is what makes AuthorTotal a total rather than a running one,
   and it is why the percentage can be computed in the same pass rather than
   joined back from a subquery.

   ROWS UNBOUNDED PRECEDING is shorthand for
   ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW.
   ============================================================================ */
PRINT '=== 5.  Running totals, a moving frame, and a partition-wide total ===';

SELECT
    Author        = a.FullName,
    q.QuoteId,
    q.CreatedAt,
    Chars         = LEN(q.QuoteText),
    RunningChars  = SUM(LEN(q.QuoteText)) OVER (w ROWS UNBOUNDED PRECEDING),
    AuthorTotal   = SUM(LEN(q.QuoteText)) OVER (PARTITION BY q.AuthorId),
    ShareSoFar    = CAST(100.0 * SUM(LEN(q.QuoteText)) OVER (w ROWS UNBOUNDED PRECEDING)
                               / SUM(LEN(q.QuoteText)) OVER (PARTITION BY q.AuthorId)
                         AS decimal(5, 1)),
    Rolling3Avg   = CAST(AVG(LEN(q.QuoteText) * 1.0) OVER (w ROWS BETWEEN 2 PRECEDING AND CURRENT ROW)
                         AS decimal(6, 1))
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName  = N'Edsger W. Dijkstra'
WINDOW w AS (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId)
ORDER BY q.CreatedAt, q.QuoteId;
GO

PRINT '--- a corpus-wide running total: no PARTITION BY, so one window over everything ---';

SELECT TOP (12)
    q.CreatedAt,
    Author         = a.FullName,
    CorpusRunning  = COUNT(*) OVER (ORDER BY q.CreatedAt, q.QuoteId ROWS UNBOUNDED PRECEDING),
    AuthorRunning  = COUNT(*) OVER (PARTITION BY q.AuthorId ORDER BY q.CreatedAt, q.QuoteId
                                    ROWS UNBOUNDED PRECEDING)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
ORDER BY q.CreatedAt, q.QuoteId;
GO

/* ============================================================================
   6.  Why this is not a GROUP BY.
   ----------------------------------------------------------------------------
   Same aggregate, same partitioning, two different result shapes. GROUP BY
   returns one row per author and the quotes are gone. The window returns every
   quote with the author's total attached to each.

   Neither is better. The distinction is whether the question is about the group
   or about the rows in it -- and a running count is unambiguously about the
   rows, because it changes on every one of them.
   ============================================================================ */
PRINT '=== 6a.  GROUP BY collapses ===';

SELECT
    Author     = a.FullName,
    QuoteCount = COUNT(*),
    TotalChars = SUM(LEN(q.QuoteText))
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName IN (N'Marie Curie', N'Ada Lovelace')
GROUP BY a.FullName
ORDER BY a.FullName;
GO

PRINT '=== 6b.  The window keeps every row ===';

SELECT
    Author     = a.FullName,
    q.QuoteId,
    QuoteCount = COUNT(*)            OVER (PARTITION BY q.AuthorId),
    TotalChars = SUM(LEN(q.QuoteText)) OVER (PARTITION BY q.AuthorId),
    Quote      = LEFT(q.QuoteText, 44)
FROM app.Quote AS q
INNER JOIN app.Author AS a ON a.AuthorId = q.AuthorId
WHERE q.IsDeleted = 0
  AND a.FullName IN (N'Marie Curie', N'Ada Lovelace')
ORDER BY a.FullName, q.CreatedAt, q.QuoteId;
GO
