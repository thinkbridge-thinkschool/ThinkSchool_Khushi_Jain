SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


   
PRINT '=== Q1-A.  Authors with quotes who have no tags at all ===';

SELECT a.AuthorId, a.FullName
FROM app.Author AS a
INNER JOIN app.Quote AS q ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0

EXCEPT

SELECT a.AuthorId, a.FullName
FROM app.Author AS a
INNER JOIN app.Quote    AS q  ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0
INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId;
GO

/* The result above is empty. An empty result set is indistinguishable, at a
   glance, from a query that is silently broken -- so here are the two operands'
   cardinalities. They are equal, which is why the difference is empty: in this
   dataset every author who has written has tagged at least one quote. */
   
PRINT '--- proof the empty result is a fact about the data, not a bug ---';

SELECT
    AuthorsWithQuotes       = (SELECT COUNT(DISTINCT q.AuthorId)
                               FROM app.Quote AS q
                               WHERE q.IsDeleted = 0),
    AuthorsWithTaggedQuotes = (SELECT COUNT(DISTINCT q.AuthorId)
                               FROM app.Quote AS q
                               INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
                               WHERE q.IsDeleted = 0);
GO

PRINT '=== Q1-B.  Authors who own at least one untagged quote ===';

/* Same operator, one grain lower. The subtraction happens over (author, quote)
   pairs, so an author survives if any single quote of theirs is untagged --
   even though every one of these authors has tagged other quotes, which is
   exactly why reading A returned nothing. */
WITH UntaggedQuotes AS
(
    SELECT q.AuthorId, q.QuoteId
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0

    EXCEPT

    SELECT q.AuthorId, q.QuoteId
    FROM app.Quote AS q
    INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
    WHERE q.IsDeleted = 0
)
SELECT
    a.FullName,
    UntaggedQuoteCount = COUNT(*),
    UntaggedQuoteIds   = STRING_AGG(CAST(u.QuoteId AS varchar(10)), ', ')
                             WITHIN GROUP (ORDER BY u.QuoteId)
FROM UntaggedQuotes AS u
INNER JOIN app.Author AS a ON a.AuthorId = u.AuthorId
GROUP BY a.FullName
ORDER BY a.FullName;
GO

/* ============================================================================
   Q2.  "Authors in both the 'classic' and 'modern' sets."   OPERATOR: INTERSECT
   ============================================================================ */
PRINT '=== Q2.  Authors present in both the classic and the modern canon ===';

WITH RootOf AS
(
    SELECT
        CategoryId = c.CategoryId,
        RootName   = CAST(c.Name AS nvarchar(80))
    FROM app.Category AS c
    WHERE c.ParentCategoryId IS NULL

    UNION ALL

    SELECT
        CategoryId = c.CategoryId,
        RootName   = p.RootName
    FROM app.Category AS c
    INNER JOIN RootOf AS p ON p.CategoryId = c.ParentCategoryId
),
ClassicCanon AS
(
    SELECT a.AuthorId, a.FullName
    FROM app.Quote AS q
    INNER JOIN RootOf     AS r ON r.CategoryId = q.CategoryId
    INNER JOIN app.Author AS a ON a.AuthorId   = q.AuthorId
    WHERE q.IsDeleted = 0
      AND r.RootName IN (N'Philosophy', N'Literature')
),
ModernCanon AS
(
    SELECT a.AuthorId, a.FullName
    FROM app.Quote AS q
    INNER JOIN RootOf     AS r ON r.CategoryId = q.CategoryId
    INNER JOIN app.Author AS a ON a.AuthorId   = q.AuthorId
    WHERE q.IsDeleted = 0
      AND r.RootName = N'Science'
)
/* No DISTINCT in either CTE on purpose: INTERSECT deduplicates its result, so
   adding one would be redundant work that also hides the fact that it does. */
SELECT AuthorId, FullName FROM ClassicCanon
INTERSECT
SELECT AuthorId, FullName FROM ModernCanon
ORDER BY FullName
OPTION (MAXRECURSION 50);
GO

PRINT '--- the three-way split: classic only, modern only, both ---';

WITH RootOf AS
(
    SELECT CategoryId = c.CategoryId, RootName = CAST(c.Name AS nvarchar(80))
    FROM app.Category AS c
    WHERE c.ParentCategoryId IS NULL
    UNION ALL
    SELECT c.CategoryId, p.RootName
    FROM app.Category AS c
    INNER JOIN RootOf AS p ON p.CategoryId = c.ParentCategoryId
),
ClassicCanon AS
(
    SELECT a.AuthorId, a.FullName
    FROM app.Quote AS q
    INNER JOIN RootOf     AS r ON r.CategoryId = q.CategoryId
    INNER JOIN app.Author AS a ON a.AuthorId   = q.AuthorId
    WHERE q.IsDeleted = 0 AND r.RootName IN (N'Philosophy', N'Literature')
),
ModernCanon AS
(
    SELECT a.AuthorId, a.FullName
    FROM app.Quote AS q
    INNER JOIN RootOf     AS r ON r.CategoryId = q.CategoryId
    INNER JOIN app.Author AS a ON a.AuthorId   = q.AuthorId
    WHERE q.IsDeleted = 0 AND r.RootName = N'Science'
)
SELECT
    ClassicOnly = (SELECT COUNT(*) FROM (SELECT AuthorId, FullName FROM ClassicCanon
                                         EXCEPT
                                         SELECT AuthorId, FullName FROM ModernCanon) AS x),
    ModernOnly  = (SELECT COUNT(*) FROM (SELECT AuthorId, FullName FROM ModernCanon
                                         EXCEPT
                                         SELECT AuthorId, FullName FROM ClassicCanon) AS y),
    Both        = (SELECT COUNT(*) FROM (SELECT AuthorId, FullName FROM ClassicCanon
                                         INTERSECT
                                         SELECT AuthorId, FullName FROM ModernCanon) AS z)
OPTION (MAXRECURSION 50);
GO

PRINT '--- the rival reading: classic and modern as author eras ---';


SELECT
    ClassicAuthors = (SELECT COUNT(*) FROM app.Author WHERE BirthYear < 1900),
    ModernAuthors  = (SELECT COUNT(*) FROM app.Author WHERE BirthYear >= 1900),
    UnknownEra     = (SELECT COUNT(*) FROM app.Author WHERE BirthYear IS NULL),
    InBothEras     = (SELECT COUNT(*) FROM (
                          SELECT AuthorId FROM app.Author WHERE BirthYear <  1900
                          INTERSECT
                          SELECT AuthorId FROM app.Author WHERE BirthYear >= 1900) AS e);
GO

/* ============================================================================
   Q3.  "The combined distinct tag list across two categories."  OPERATOR: UNION
   ============================================================================ */
PRINT '=== Q3.  Combined distinct tag list: Algorithms + Software Engineering ===';

SELECT t.TagId, TagName = t.Name
FROM app.Quote        AS q
INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
INNER JOIN app.Tag      AS t  ON t.TagId    = qt.TagId
INNER JOIN app.Category AS c  ON c.CategoryId = q.CategoryId
WHERE q.IsDeleted = 0
  AND c.Name      = N'Algorithms'

UNION

SELECT t.TagId, TagName = t.Name
FROM app.Quote        AS q
INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
INNER JOIN app.Tag      AS t  ON t.TagId    = qt.TagId
INNER JOIN app.Category AS c  ON c.CategoryId = q.CategoryId
WHERE q.IsDeleted = 0
  AND c.Name      = N'Software Engineering'

ORDER BY TagName;
GO

PRINT '--- what UNION ALL would have returned instead ---';


SELECT
    ViaUnion    = (SELECT COUNT(*) FROM (
                       SELECT qt.TagId FROM app.Quote q
                       JOIN app.QuoteTag qt ON qt.QuoteId = q.QuoteId
                       JOIN app.Category c ON c.CategoryId = q.CategoryId
                       WHERE q.IsDeleted = 0 AND c.Name = N'Algorithms'
                       UNION
                       SELECT qt.TagId FROM app.Quote q
                       JOIN app.QuoteTag qt ON qt.QuoteId = q.QuoteId
                       JOIN app.Category c ON c.CategoryId = q.CategoryId
                       WHERE q.IsDeleted = 0 AND c.Name = N'Software Engineering') AS u),
    ViaUnionAll = (SELECT COUNT(*) FROM (
                       SELECT qt.TagId FROM app.Quote q
                       JOIN app.QuoteTag qt ON qt.QuoteId = q.QuoteId
                       JOIN app.Category c ON c.CategoryId = q.CategoryId
                       WHERE q.IsDeleted = 0 AND c.Name = N'Algorithms'
                       UNION ALL
                       SELECT qt.TagId FROM app.Quote q
                       JOIN app.QuoteTag qt ON qt.QuoteId = q.QuoteId
                       JOIN app.Category c ON c.CategoryId = q.CategoryId
                       WHERE q.IsDeleted = 0 AND c.Name = N'Software Engineering') AS ua);
GO

/* ============================================================================
   4.  Three behaviours of set operators that bite.
   ============================================================================ */
PRINT '=== 4a.  Set operators compare NULLs as equal. Joins do not. ===';



SELECT FullName, BirthYear FROM app.Author WHERE BirthYear IS NULL
EXCEPT
SELECT FullName, BirthYear FROM app.Author WHERE BirthYear IS NULL;
GO

PRINT '--- the same thing as an anti-join, which does not match NULL to NULL ---';

SELECT a.FullName, a.BirthYear
FROM app.Author AS a
LEFT JOIN app.Author AS b
       ON b.FullName  = a.FullName
      AND b.BirthYear = a.BirthYear      -- UNKNOWN whenever BirthYear is NULL
WHERE a.BirthYear IS NULL
  AND b.AuthorId IS NULL
ORDER BY a.FullName;
GO

PRINT '=== 4b.  INTERSECT binds tighter than UNION and EXCEPT ===';

/* Written without parentheses, INTERSECT is evaluated first, so this reads
   {1,2} UNION ({2,3} INTERSECT {3,4})  =  {1,2} UNION {3}  =  {1,2,3}. */
SELECT v FROM (VALUES (1), (2)) AS a(v)
UNION
SELECT v FROM (VALUES (2), (3)) AS b(v)
INTERSECT
SELECT v FROM (VALUES (3), (4)) AS c(v);
GO

PRINT '--- forcing left-to-right with parentheses gives a different answer ---';

/* ({1,2} UNION {2,3}) INTERSECT {3,4}  =  {1,2,3} INTERSECT {3,4}  =  {3}. */
SELECT v FROM (
    SELECT v FROM (VALUES (1), (2)) AS a(v)
    UNION
    SELECT v FROM (VALUES (2), (3)) AS b(v)
) AS unioned
INTERSECT
SELECT v FROM (VALUES (3), (4)) AS c(v);
GO

PRINT '=== 4c.  Column names come from the first query; ORDER BY sorts the whole set ===';

/* The second SELECT calls the column something else and it makes no difference:
   the result is named by the first. ORDER BY belongs to the set expression as a
   whole, may appear only at the very end, and must refer to the first query's
   names -- ordering by TagLabel here would be an error. */
SELECT TagName = t.Name FROM app.Tag AS t WHERE t.TagId <= 2
UNION
SELECT TagLabel = t.Name FROM app.Tag AS t WHERE t.TagId >= 7
ORDER BY TagName;
GO


PRINT '=== 5.  Three spellings of the same question, and their agreement ===';

WITH ViaExcept AS
(
    SELECT q.AuthorId, q.QuoteId FROM app.Quote AS q WHERE q.IsDeleted = 0
    EXCEPT
    SELECT q.AuthorId, q.QuoteId
    FROM app.Quote AS q
    INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
    WHERE q.IsDeleted = 0
),
ViaNotExists AS
(
    SELECT q.AuthorId, q.QuoteId
    FROM app.Quote AS q
    WHERE q.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM app.QuoteTag AS qt WHERE qt.QuoteId = q.QuoteId)
),
ViaAntiJoin AS
(
    SELECT q.AuthorId, q.QuoteId
    FROM app.Quote AS q
    LEFT JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
    WHERE q.IsDeleted = 0
      AND qt.QuoteId IS NULL
)
SELECT
    ExceptRows      = (SELECT COUNT(*) FROM ViaExcept),
    NotExistsRows   = (SELECT COUNT(*) FROM ViaNotExists),
    AntiJoinRows    = (SELECT COUNT(*) FROM ViaAntiJoin),
    DisagreementRows = (SELECT COUNT(*) FROM (
                            SELECT * FROM ViaExcept
                            EXCEPT
                            SELECT * FROM ViaNotExists) AS d1)
                     + (SELECT COUNT(*) FROM (
                            SELECT * FROM ViaNotExists
                            EXCEPT
                            SELECT * FROM ViaAntiJoin) AS d2);
GO
