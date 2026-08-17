/* ============================================================================
   Day 7 — the plans behind the three shapes
   ----------------------------------------------------------------------------
   Section 4 of 04_author_quote_summary.sql claims the window version and the
   OUTER APPLY version have different costs at different scales. This file is
   the evidence rather than the assertion.

   SHOWPLAN_TEXT returns estimated plans without executing anything, so this
   script produces no result rows. It has to be the only statement in its batch,
   which is why the GO fences below are not optional.

   At the seed's eighty rows every plan here is instant, so the numbers are
   meaningless and only the shapes are worth reading. What the shapes show is
   where each query would go wrong first if the table grew.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

SET SHOWPLAN_TEXT ON;
GO

/* ---------------------------------------------------------------------------
   Plan A — the submitted window version.

   What to look for: an Index Scan of IX_Quote_Author_CreatedAt marked ORDERED
   FORWARD feeding Segment and Sequence Project, with no Sort between them. The
   index key is (AuthorId, CreatedAt DESC, QuoteId DESC), which is exactly the
   window's PARTITION BY plus ORDER BY, so the rows arrive already in the order
   ROW_NUMBER needs and the ranking is free.

   That is the whole reason QuoteId DESC sits in the index key rather than in
   the INCLUDE list. A tiebreaker outside the key order would still be correct,
   and would reintroduce the sort this plan avoids.

   The one Sort at the top is the final ORDER BY QuoteCount DESC, which cannot
   be indexed away because the count does not exist until the window is
   computed.
   --------------------------------------------------------------------------- */
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

/* ---------------------------------------------------------------------------
   Plan B — the OUTER APPLY version.

   Two halves that behave very differently, which is the point of running this.

   The APPLY half is as good as advertised: Index Seek on
   IX_Quote_Author_CreatedAt seeking AuthorId, ORDERED FORWARD, under a Top(1).
   One seek per author, one row read, no sort. It does not care how many quotes
   an author has.

   The aggregate half is the problem. QuoteStats is referenced once, so the
   optimiser inlines it rather than spooling it, and it becomes a Clustered
   Index Scan of PK_Quote with a residual predicate on AuthorId, driven by
   nested loops -- one scan of the whole table per author. IX_Quote_CategoryId
   and the filtered index are both ignored because neither leads with what this
   half needs.

   So "APPLY is cheaper at scale" is only half true, and the half that is true
   is not the half that dominates. Making APPLY genuinely win would mean
   sourcing the count from somewhere that is not a per-author scan: an indexed
   view, a maintained counter column, or a single pre-aggregated pass whose
   result is materialised before the join.
   --------------------------------------------------------------------------- */
WITH QuoteStats AS
(
    SELECT
        q.AuthorId,
        QuoteCount = COUNT(*)
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
    GROUP BY q.AuthorId
)
SELECT
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

/* ---------------------------------------------------------------------------
   Plan C — the correlated-subquery version.

   Three separate correlated executions per author, which is what the exercise
   asked to be replaced. Worth reading beside plan A to see the difference
   stated in operators rather than in prose.
   --------------------------------------------------------------------------- */
SELECT
    a.AuthorId,
    a.FullName,
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
ORDER BY QuoteCount DESC, a.FullName;
GO

SET SHOWPLAN_TEXT OFF;
GO
