SET NOCOUNT ON;
GO

USE QuotesLab;
GO

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
GO
