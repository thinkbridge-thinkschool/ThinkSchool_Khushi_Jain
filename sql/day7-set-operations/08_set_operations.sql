/* ============================================================================
   Day 7 — set operations from a spec
   ----------------------------------------------------------------------------
   Three business questions, translated. The SQL is the easy half; the hard half
   is that two of the three questions do not have a single meaning, and the
   third names sets that do not exist in this schema at all.

     Q1  "authors with quotes but no tags"
     Q2  "authors in both the 'classic' and 'modern' sets"
     Q3  "the combined distinct tag list across two categories"

   Where a question is ambiguous, both readings are answered rather than one
   quietly chosen. Where the vocabulary has no counterpart in the schema, the
   mapping is written down in the query as a named CTE, so a reviewer can
   disagree with the translation instead of having to reverse-engineer it.

   Nothing in this file modifies the seed. Pieces 1 and 2 of Day 7 have already
   been submitted against that data, and reshaping a shared fixture so a query
   returns prettier rows would invalidate their captured evidence. Where an
   honest answer is the empty set, the empty set is what this returns, with the
   input cardinalities printed beside it so the emptiness is visibly a fact
   about the data rather than a broken join.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   Q1.  "Authors with quotes but no tags."          OPERATOR: EXCEPT
   ----------------------------------------------------------------------------
   EXCEPT, because the question is literally a subtraction: start from the
   authors who have written, remove the ones that have tags. Written as a join
   or a NOT EXISTS it is the same answer, but EXCEPT is the only form whose
   shape matches the shape of the sentence, and this piece is about translation.

   The sentence has two readings and they give different answers:

     A.  Authors who have quotes, and have no tags at all.
     B.  Authors who have quotes that carry no tags.

   A is the more natural English parse. B is what someone auditing tag coverage
   usually means. The difference is grain: A subtracts sets of authors, B
   subtracts sets of (author, quote) pairs and then asks who is left.
   ============================================================================ */
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
   ----------------------------------------------------------------------------
   INTERSECT, because "in both" is membership of two sets at once, and that is
   the operator whose definition is that sentence.

   The translation problem: QuotesLab has no 'classic' or 'modern' marker. No
   column, no tag, no collection. So the sets have to be defined, and the
   definition is a judgement call that belongs in the open where it can be
   argued with.

   The mapping used here reads the two canons off the category tree:

       classic  =  authors with a live quote under the Philosophy or
                   Literature roots  (ethics, logic, stoicism, fiction, poetry)
       modern   =  authors with a live quote under the Science root
                   (physics, mathematics, computer science and below)

   RootOf recurses the category tree to attribute every category to its root,
   so a quote filed under Concurrency counts toward Science three levels up.
   That is the same traversal as piece 1's section 5, reused rather than
   restated.

   A different and equally defensible reading is below, and it is worth running
   because it changes the answer to "nobody".
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

/* If "classic" and "modern" describe the author rather than the subject matter,
   the obvious column is BirthYear -- and the answer becomes zero by
   construction, because no one is born in two centuries. The operator is still
   right; the definition makes the question unanswerable.

   This is worth showing rather than discarding. A business question that
   presumes overlap, translated onto a rule that forbids it, returns an empty
   set that looks exactly like a data problem. The fix is not SQL, it is going
   back and asking what 'classic' means. */
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
   ----------------------------------------------------------------------------
   UNION, not UNION ALL. The word doing the work is "distinct": a tag used in
   both categories must appear once. UNION deduplicates, UNION ALL concatenates.

   Categories chosen: Algorithms and Software Engineering, two siblings under
   Computer Science with genuinely different tag profiles.
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

/* The row counts are the argument. UNION ALL keeps every occurrence, so a tag
   applied to six quotes appears six times and the "distinct tag list" is not a
   list of distinct tags. It is also the cheaper operator, which is why it is
   the right default everywhere the duplicates are known to be impossible --
   just not here. */
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

/* Three authors have a NULL BirthYear. EXCEPT matches them against themselves
   and removes them, returning nothing -- so EXCEPT is using NULL = NULL. Under
   the comparison rules a join uses, NULL = NULL is UNKNOWN, nothing would match
   and all three rows would survive.

   The second query is the same intent expressed as an anti-join, and it returns
   those three rows. Same data, same question, opposite answer, because the two
   constructs disagree about what NULL means. */
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

/* ============================================================================
   5.  EXCEPT, NOT EXISTS and the anti-join — same answer, different costs.
   ----------------------------------------------------------------------------
   All three express Q1-B. They are interchangeable here because the projected
   columns are already unique, and that is the condition worth remembering:
   EXCEPT applies DISTINCT to its result whether or not you wanted it, so on a
   set that legitimately contains duplicates it silently collapses them, while
   NOT EXISTS leaves the outer row count alone.

   Prefer EXCEPT when the sentence is a subtraction and the dedup is harmless or
   wanted. Prefer NOT EXISTS when the outer side has to keep its cardinality, or
   when the two sides do not have the same column list.
   ============================================================================ */
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
