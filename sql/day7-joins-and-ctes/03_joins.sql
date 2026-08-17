/* ============================================================================
   Day 7 — joins at depth
   ----------------------------------------------------------------------------
   Inner, left, anti and cross joins against QuotesLab, each one paired with the
   mistake it is usually the victim of. The point is not that LEFT JOIN keeps
   unmatched rows -- everyone knows that -- but that the three ways of losing
   them are silent: an INNER JOIN over a nullable foreign key, a right-table
   predicate stranded in WHERE, and a COUNT(*) taken after a one-to-many join.
   None of the three raises an error. All three change the number.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------------------
   3.1  INNER JOIN, and what it costs over a nullable foreign key.

   Quote.CategoryId is nullable. Joining through it with INNER JOIN produces a
   perfectly sensible-looking report that is missing every uncategorised quote,
   with nothing in the output to say so.
   --------------------------------------------------------------------------- */
PRINT '=== 3.1  INNER JOIN drops rows on a nullable FK ===';

SELECT
    LiveQuotes         = (SELECT COUNT(*) FROM app.Quote WHERE IsDeleted = 0),
    ViaInnerJoin       = (SELECT COUNT(*)
                          FROM app.Quote AS q
                          INNER JOIN app.Category AS c ON c.CategoryId = q.CategoryId
                          WHERE q.IsDeleted = 0),
    ViaLeftJoin        = (SELECT COUNT(*)
                          FROM app.Quote AS q
                          LEFT JOIN app.Category AS c ON c.CategoryId = q.CategoryId
                          WHERE q.IsDeleted = 0);
GO

PRINT '--- the rows the inner join silently removed ---';

SELECT TOP (10)
    q.QuoteId,
    a.FullName,
    CategoryName = COALESCE(c.Name, N'(uncategorised)'),
    Preview      = LEFT(q.QuoteText, 52)
FROM app.Quote AS q
INNER JOIN app.Author   AS a ON a.AuthorId   = q.AuthorId    -- every quote has an author: inner is right here
LEFT  JOIN app.Category AS c ON c.CategoryId = q.CategoryId  -- not every quote has a category
WHERE q.IsDeleted = 0
  AND q.CategoryId IS NULL
ORDER BY q.QuoteId;
GO

/* ---------------------------------------------------------------------------
   3.2  INNER vs LEFT on the author side.

   Three authors have no live quotes. An INNER JOIN answers "authors who have
   written" while claiming to answer "authors". The gap is the whole exercise.
   --------------------------------------------------------------------------- */
PRINT '=== 3.2  INNER vs LEFT — how many authors does each report? ===';

SELECT
    AuthorsInTable   = (SELECT COUNT(*) FROM app.Author),
    ViaInnerJoin     = (SELECT COUNT(DISTINCT a.AuthorId)
                        FROM app.Author AS a
                        INNER JOIN app.Quote AS q ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0),
    ViaLeftJoin      = (SELECT COUNT(DISTINCT a.AuthorId)
                        FROM app.Author AS a
                        LEFT JOIN app.Quote AS q ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0);
GO

/* ---------------------------------------------------------------------------
   3.3  LEFT JOIN done properly — every author, quote count, zeroes included.

   COUNT(q.QuoteId) rather than COUNT(*): after a LEFT JOIN the unmatched rows
   still exist, NULL-extended, so COUNT(*) would report 1 for an author with
   nothing. Counting a column from the right-hand table ignores the NULLs and
   reports 0, which is the true answer.
   --------------------------------------------------------------------------- */
PRINT '=== 3.3  LEFT JOIN — every author, including the empty ones ===';

SELECT
    a.AuthorId,
    a.FullName,
    a.Nationality,
    a.BirthYear,
    CountedWrong = COUNT(*),            -- inflates an author with no quotes to 1
    QuoteCount   = COUNT(q.QuoteId)     -- correct: NULLs are not counted
FROM app.Author AS a
LEFT JOIN app.Quote AS q
       ON q.AuthorId  = a.AuthorId
      AND q.IsDeleted = 0
GROUP BY a.AuthorId, a.FullName, a.Nationality, a.BirthYear
ORDER BY QuoteCount ASC, a.FullName;
GO

/* ---------------------------------------------------------------------------
   3.4  The join done wrong: a right-table predicate in WHERE.

   A LEFT JOIN NULL-extends unmatched rows, then WHERE runs afterwards. Any
   predicate on the right table other than IS NULL therefore evaluates to
   UNKNOWN for those rows and throws them away -- quietly converting the outer
   join back into an inner one. The predicate has to move into ON, where it is
   applied while the match is being decided.

   Hypatia is the tell: her only quote is soft-deleted, so the WHERE version
   loses her entirely while the ON version keeps her at zero.
   --------------------------------------------------------------------------- */
PRINT '=== 3.4  IsDeleted in WHERE vs in ON ===';

PRINT '--- wrong: LEFT JOIN silently demoted to INNER JOIN ---';
SELECT AuthorsReturned = COUNT(*)
FROM (
    SELECT a.AuthorId
    FROM app.Author AS a
    LEFT JOIN app.Quote AS q ON q.AuthorId = a.AuthorId
    WHERE q.IsDeleted = 0
    GROUP BY a.AuthorId
) AS x;
GO

PRINT '--- right: the predicate lives in ON, the outer join survives ---';
SELECT AuthorsReturned = COUNT(*)
FROM (
    SELECT a.AuthorId
    FROM app.Author AS a
    LEFT JOIN app.Quote AS q ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0
    GROUP BY a.AuthorId
) AS x;
GO

/* ---------------------------------------------------------------------------
   3.5  Anti-join — authors with nothing live to their name.

   LEFT JOIN ... WHERE right-key IS NULL is the one case where a right-table
   predicate in WHERE is correct, because IS NULL is exactly the test for
   "this row did not match". NOT EXISTS expresses the same thing and usually
   gets the same plan; it is included because it reads better.
   --------------------------------------------------------------------------- */
PRINT '=== 3.5  Anti-join — authors with no live quotes ===';

SELECT
    a.AuthorId,
    a.FullName,
    a.Nationality
FROM app.Author AS a
LEFT JOIN app.Quote AS q
       ON q.AuthorId  = a.AuthorId
      AND q.IsDeleted = 0
WHERE q.QuoteId IS NULL
ORDER BY a.FullName;
GO

PRINT '--- same answer, stated as NOT EXISTS ---';

SELECT
    a.AuthorId,
    a.FullName
FROM app.Author AS a
WHERE NOT EXISTS (
    SELECT 1
    FROM app.Quote AS q
    WHERE q.AuthorId  = a.AuthorId
      AND q.IsDeleted = 0
)
ORDER BY a.FullName;
GO

/* ---------------------------------------------------------------------------
   3.6  One-to-many, and the counting bug it plants.

   Joining QuoteTag multiplies each quote by its tag count. That is correct --
   the join is doing what it was asked. What is wrong is aggregating over the
   multiplied rows and calling the result a quote count.
   --------------------------------------------------------------------------- */
PRINT '=== 3.6  Row multiplication across a many-to-many ===';

SELECT TOP (10)
    a.FullName,
    QuoteCountInflated = COUNT(*),                     -- counts quote-tag pairs
    QuoteCountCorrect  = COUNT(DISTINCT q.QuoteId),    -- counts quotes
    TagLinks           = COUNT(qt.TagId)
FROM app.Author AS a
INNER JOIN app.Quote    AS q  ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0
LEFT  JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId
GROUP BY a.AuthorId, a.FullName
ORDER BY QuoteCountInflated DESC, a.FullName;
GO

/* ---------------------------------------------------------------------------
   3.7  CROSS JOIN with a purpose — a coverage matrix.

   A cross join is not only the accident you get from a missing ON clause. It is
   the correct tool when you need rows that do not exist yet: every author
   crossed with every tag gives 19 x 8 = 152 cells, and the LEFT JOIN then fills
   in the few that have data. Aggregating the raw QuoteTag table can only ever
   report combinations that occurred; it cannot report an absence.
   --------------------------------------------------------------------------- */
PRINT '=== 3.7  CROSS JOIN — author x tag coverage grid ===';

SELECT
    GridCells        = COUNT(*),
    CellsWithUsage   = SUM(CASE WHEN Uses > 0 THEN 1 ELSE 0 END),
    CellsEmpty       = SUM(CASE WHEN Uses = 0 THEN 1 ELSE 0 END)
FROM (
    SELECT
        a.AuthorId,
        t.TagId,
        Uses = COUNT(qt.QuoteId)
    FROM app.Author AS a
    CROSS JOIN app.Tag AS t
    LEFT  JOIN app.Quote    AS q  ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0
    LEFT  JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId  AND qt.TagId    = t.TagId
    GROUP BY a.AuthorId, t.TagId
) AS grid;
GO

PRINT '--- who has never used the "testing" tag ---';

SELECT TOP (10)
    a.FullName,
    Tag  = t.Name,
    Uses = COUNT(qt.QuoteId)
FROM app.Author AS a
CROSS JOIN app.Tag AS t
LEFT  JOIN app.Quote    AS q  ON q.AuthorId = a.AuthorId AND q.IsDeleted = 0
LEFT  JOIN app.QuoteTag AS qt ON qt.QuoteId = q.QuoteId  AND qt.TagId    = t.TagId
WHERE t.Name = N'testing'
GROUP BY a.FullName, t.Name
HAVING COUNT(qt.QuoteId) = 0
ORDER BY a.FullName;
GO

/* ---------------------------------------------------------------------------
   3.8  A four-hop chain: user -> collection -> item -> quote -> author.

   Inner where the relationship is mandatory, left where it is not. "Empty
   shelf" has no items and must still appear, which is what forces the outer
   joins from CollectionItem onward.
   --------------------------------------------------------------------------- */
PRINT '=== 3.8  Chained joins, mandatory inner and optional outer ===';

SELECT
    Owner           = u.DisplayName,
    Collection      = c.Name,
    Items           = COUNT(ci.QuoteId),
    DistinctAuthors = COUNT(DISTINCT q.AuthorId),
    NewestAddedAt   = MAX(ci.AddedAt)
FROM app.AppUser AS u
INNER JOIN app.Collection     AS c  ON c.OwnerUserId  = u.UserId        -- a collection must have an owner
LEFT  JOIN app.CollectionItem AS ci ON ci.CollectionId = c.CollectionId -- a collection may be empty
LEFT  JOIN app.Quote          AS q  ON q.QuoteId       = ci.QuoteId
                                   AND q.IsDeleted     = 0
GROUP BY u.DisplayName, c.Name
ORDER BY Items DESC, c.Name;
GO
