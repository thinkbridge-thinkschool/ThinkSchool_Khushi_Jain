
SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

SET SHOWPLAN_TEXT ON;
GO


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
