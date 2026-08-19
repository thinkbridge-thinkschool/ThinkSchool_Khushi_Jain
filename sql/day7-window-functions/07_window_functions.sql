/* ============================================================================
   Day 7 — window functions
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
