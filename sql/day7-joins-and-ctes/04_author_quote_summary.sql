/* ============================================================================
   Day 7 — the exercise
   ----------------------------------------------------------------------------
   "Return each author with their quote count and their most-recent quote, in
    one statement, using a CTE rather than a correlated subquery in the SELECT."

   Section 1 is the answer. Sections 2 to 5 are the reasoning that produced it:
   the two shapes it replaces, the two shapes it competes with, and a proof that
   the rewrite did not change the result.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   1.  THE ANSWER — one statement, one pass over app.Quote.
   ----------------------------------------------------------------------------
   The CTE computes both facts in a single scan. COUNT(*) OVER (PARTITION BY
   AuthorId) attaches the author's total to every one of that author's rows;
   ROW_NUMBER() over the same partition, ordered newest-first, marks which row
   is the latest. The outer query then keeps only row 1 per author, so the count
   arrives with the quote it belongs to and neither is fetched twice.

   Three details that are load-bearing rather than decorative:

     * LEFT JOIN, not INNER. Hypatia, Seneca and Sun Tzu have no live quotes and
       the question asked for each author, not each author who has written. They
       come back with QuoteCount 0 and a NULL quote.
     * QuoteId DESC as the second ORDER BY term. Ada Lovelace has two quotes at
       exactly the same CreatedAt; ordering on the timestamp alone leaves
       ROW_NUMBER free to pick either one, and it may pick differently on the
       next run or on a different plan. The tiebreaker makes the answer a fact
       rather than a coincidence.
     * IsDeleted = 0 inside the CTE, which is the only place it can go. Mark
       Twain's most recent quote by date is soft-deleted, so a query that skips
       this filter returns a different quote for him without complaining.
   ============================================================================ */
PRINT '=== 1.  Author summary via CTE (the deliverable) ===';

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
GO

/* ============================================================================
   2.  What it replaces — correlated subqueries in the SELECT list.
   ----------------------------------------------------------------------------
   Same output, three correlated subqueries. Each one is re-evaluated per author
   row, and the last two are the identical TOP (1) lookup executed twice because
   a scalar subquery can only return one column. The optimiser will sometimes
   collapse them; relying on it is the problem.

   The readability cost is the more durable one. The row this query describes is
   assembled from three independent lookups that nothing forces to agree, so a
   later edit to one filter and not the others produces a count and a quote
   drawn from different populations -- a bug with no symptom except a wrong
   number.
   ============================================================================ */
PRINT '=== 2.  The correlated-subquery version this replaces ===';

SELECT
    a.AuthorId,
    a.FullName,
    QuoteCount        = (SELECT COUNT(*)
                         FROM app.Quote AS q
                         WHERE q.AuthorId  = a.AuthorId
                           AND q.IsDeleted = 0),
    MostRecentQuote   = (SELECT TOP (1) q.QuoteText
                         FROM app.Quote AS q
                         WHERE q.AuthorId  = a.AuthorId
                           AND q.IsDeleted = 0
                         ORDER BY q.CreatedAt DESC, q.QuoteId DESC),
    MostRecentQuoteAt = (SELECT TOP (1) q.CreatedAt
                         FROM app.Quote AS q
                         WHERE q.AuthorId  = a.AuthorId
                           AND q.IsDeleted = 0
                         ORDER BY q.CreatedAt DESC, q.QuoteId DESC)
FROM app.Author AS a
ORDER BY QuoteCount DESC, a.FullName;
GO

/* ============================================================================
   3.  The tempting wrong answer — GROUP BY MAX, then join back.
   ----------------------------------------------------------------------------
   Aggregate to the newest timestamp per author, then rejoin to find the row
   holding it. It reads well and it is wrong whenever the maximum is not unique:
   the rejoin matches every tied row. Ada Lovelace has two quotes at
   2026-07-04 10:00:00, so she comes back twice and the "one row per author"
   contract is broken by data the query never mentions.

   This is the failure that ROW_NUMBER with a tiebreaker exists to prevent.
   ============================================================================ */
PRINT '=== 3.  MAX-then-rejoin duplicates authors on a tied timestamp ===';

WITH LatestAt AS
(
    SELECT
        q.AuthorId,
        LastCreatedAt = MAX(q.CreatedAt)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
)
SELECT
    RowsReturned          = COUNT(*),
    AuthorsWithLiveQuotes = COUNT(DISTINCT a.AuthorId)
FROM app.Author AS a
INNER JOIN LatestAt  AS l ON l.AuthorId = a.AuthorId
INNER JOIN app.Quote AS q ON q.AuthorId  = a.AuthorId
                         AND q.CreatedAt = l.LastCreatedAt
                         AND q.IsDeleted = 0;
GO

PRINT '--- the author it duplicates ---';

WITH LatestAt AS
(
    SELECT
        q.AuthorId,
        LastCreatedAt = MAX(q.CreatedAt)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
)
SELECT
    a.FullName,
    q.QuoteId,
    q.CreatedAt,
    q.QuoteText
FROM app.Author AS a
INNER JOIN LatestAt  AS l ON l.AuthorId = a.AuthorId
INNER JOIN app.Quote AS q ON q.AuthorId  = a.AuthorId
                         AND q.CreatedAt = l.LastCreatedAt
                         AND q.IsDeleted = 0
WHERE a.AuthorId IN (
    SELECT a2.AuthorId
    FROM app.Author AS a2
    INNER JOIN LatestAt  AS l2 ON l2.AuthorId = a2.AuthorId
    INNER JOIN app.Quote AS q2 ON q2.AuthorId  = a2.AuthorId
                              AND q2.CreatedAt = l2.LastCreatedAt
                              AND q2.IsDeleted = 0
    GROUP BY a2.AuthorId
    HAVING COUNT(*) > 1
)
ORDER BY a.FullName, q.QuoteId;
GO

/* ============================================================================
   4.  Two shapes that are also correct, and when to prefer them.
   ----------------------------------------------------------------------------
   4a. Two CTEs — aggregate separately from ranking. Slightly more code and a
       second pass over the table, but the right choice the moment the count and
       the "latest" are taken over different populations. Count every quote
       including deleted ones, but show the latest live one, and the single-CTE
       form cannot express it because one WHERE clause has to serve both.

   4b. OUTER APPLY — the T-SQL idiom for "the top row of a correlated set". It
       is a correlated join rather than a correlated scalar subquery: the TOP (1)
       runs once per author and returns as many columns as you like, which
       removes the duplicated lookup that section 2 suffers from. On a good
       index it often produces the cheapest plan of the three, because it seeks
       one row per author instead of ranking every row.

   The window version is still the answer submitted: it is one pass, and it
   scales to "second-most-recent" or "top 3 per author" by changing a number,
   where APPLY needs the TOP rewritten and the subquery version needs another
   column bolted on.
   ============================================================================ */
PRINT '=== 4a.  Two-CTE variant — aggregate and rank separately ===';

WITH QuoteStats AS
(
    SELECT
        q.AuthorId,
        QuoteCount = COUNT(*)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
),
RankedQuotes AS
(
    SELECT
        q.AuthorId,
        q.QuoteText,
        q.CreatedAt,
        Recency = ROW_NUMBER() OVER (
                      PARTITION BY q.AuthorId
                      ORDER BY     q.CreatedAt DESC, q.QuoteId DESC)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
)
SELECT TOP (10)
    a.AuthorId,
    a.FullName,
    QuoteCount        = COALESCE(s.QuoteCount, 0),
    MostRecentQuote   = r.QuoteText,
    MostRecentQuoteAt = r.CreatedAt
FROM app.Author AS a
LEFT JOIN QuoteStats   AS s ON s.AuthorId = a.AuthorId
LEFT JOIN RankedQuotes AS r ON r.AuthorId = a.AuthorId AND r.Recency = 1
ORDER BY QuoteCount DESC, a.FullName;
GO

PRINT '=== 4b.  OUTER APPLY variant — one seek per author ===';

WITH QuoteStats AS
(
    SELECT
        q.AuthorId,
        QuoteCount = COUNT(*)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
)
SELECT TOP (10)
    a.AuthorId,
    a.FullName,
    QuoteCount        = COALESCE(s.QuoteCount, 0),
    MostRecentQuote   = latest.QuoteText,
    MostRecentQuoteAt = latest.CreatedAt
FROM app.Author AS a
LEFT JOIN QuoteStats AS s ON s.AuthorId = a.AuthorId
OUTER APPLY (
    SELECT TOP (1)
        q.QuoteText,
        q.CreatedAt
    FROM app.Quote AS q
    WHERE q.AuthorId  = a.AuthorId
      AND q.IsDeleted = 0
    ORDER BY q.CreatedAt DESC, q.QuoteId DESC
) AS latest
ORDER BY QuoteCount DESC, a.FullName;
GO

/* ============================================================================
   5.  Proof the rewrite preserved behaviour.
   ----------------------------------------------------------------------------
   EXCEPT in both directions between the window version and the correlated
   version. EXCEPT compares NULLs as equal, so the three authors with no quotes
   are checked properly rather than dropped. Both counts must be zero; anything
   else means the rewrite changed the answer.
   ============================================================================ */
PRINT '=== 5.  Equivalence check — both differences must be 0 ===';

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
),
ViaWindow AS
(
    SELECT
        a.AuthorId,
        QuoteCount        = COALESCE(aq.AuthorQuoteCount, 0),
        MostRecentQuote   = aq.QuoteText,
        MostRecentQuoteAt = aq.CreatedAt
    FROM app.Author AS a
    LEFT JOIN AuthorQuote AS aq
           ON aq.AuthorId = a.AuthorId
          AND aq.Recency  = 1
),
ViaCorrelated AS
(
    SELECT
        a.AuthorId,
        QuoteCount        = (SELECT COUNT(*)
                             FROM app.Quote AS q
                             WHERE q.AuthorId = a.AuthorId AND q.IsDeleted = 0),
        MostRecentQuote   = (SELECT TOP (1) q.QuoteText
                             FROM app.Quote AS q
                             WHERE q.AuthorId = a.AuthorId AND q.IsDeleted = 0
                             ORDER BY q.CreatedAt DESC, q.QuoteId DESC),
        MostRecentQuoteAt = (SELECT TOP (1) q.CreatedAt
                             FROM app.Quote AS q
                             WHERE q.AuthorId = a.AuthorId AND q.IsDeleted = 0
                             ORDER BY q.CreatedAt DESC, q.QuoteId DESC)
    FROM app.Author AS a
)
SELECT
    InWindowNotCorrelated = (SELECT COUNT(*) FROM (
                                 SELECT * FROM ViaWindow
                                 EXCEPT
                                 SELECT * FROM ViaCorrelated) AS d1),
    InCorrelatedNotWindow = (SELECT COUNT(*) FROM (
                                 SELECT * FROM ViaCorrelated
                                 EXCEPT
                                 SELECT * FROM ViaWindow) AS d2);
GO
