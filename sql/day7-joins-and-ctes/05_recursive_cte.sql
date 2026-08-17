/* ============================================================================
   Day 7 — recursive and non-recursive CTEs
   ----------------------------------------------------------------------------
   app.Category is the only structure in QuotesLab that cannot be traversed to
   arbitrary depth by a fixed number of joins. Four levels today means four
   self-joins; a fifth level added next week breaks the query silently by
   returning the first four. Recursion is what removes the dependency on
   knowing the depth in advance.

   Sections 1 to 4 recurse. Section 5 does not, and is here because most CTEs
   in real code are the non-recursive kind: named stages that keep a query
   readable, not a traversal.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   1.  The tree, with depth and a materialised path.
   ----------------------------------------------------------------------------
   Anchor member: the roots, the rows whose parent is NULL. Recursive member:
   every child of a row the CTE has already produced, joined back to the CTE by
   name. UNION ALL is mandatory -- UNION is not permitted in a recursive CTE,
   and would be wrong anyway since deduplicating would hide a cycle rather than
   stop it.

   Path is built by concatenation as the recursion descends, which is what makes
   ORDER BY Path print the tree in reading order. The CAST in the anchor is not
   optional: it fixes the column's type and length for the whole recursion, and
   without it SQL Server infers nvarchar(80) from Category.Name and then fails
   the recursive member with a truncation error on the first concatenation.
   ============================================================================ */
PRINT '=== 1.  Category tree — depth, path, root ===';

WITH CategoryTree AS
(
    SELECT
        c.CategoryId,
        c.Name,
        c.ParentCategoryId,
        Depth    = 0,
        Path     = CAST(c.Name AS nvarchar(400)),
        RootName = CAST(c.Name AS nvarchar(80))
    FROM app.Category AS c
    WHERE c.ParentCategoryId IS NULL

    UNION ALL

    SELECT
        c.CategoryId,
        c.Name,
        c.ParentCategoryId,
        Depth    = p.Depth + 1,
        Path     = CAST(p.Path + N' > ' + c.Name AS nvarchar(400)),
        RootName = p.RootName
    FROM app.Category  AS c
    INNER JOIN CategoryTree AS p ON p.CategoryId = c.ParentCategoryId
)
SELECT
    t.CategoryId,
    t.Depth,
    Indented = REPLICATE(N'  ', t.Depth) + t.Name,
    t.RootName,
    t.Path
FROM CategoryTree AS t
ORDER BY t.Path
OPTION (MAXRECURSION 50);
GO

/* ============================================================================
   2.  Roll-up — quotes in a category and everything beneath it.
   ----------------------------------------------------------------------------
   The question a plain GROUP BY cannot answer. "How many quotes are filed under
   Science" means Science plus Physics plus Mathematics plus Computer Science
   plus Algorithms plus Distributed Systems plus Concurrency plus the rest, and
   that set is only knowable by walking the tree.

   Descendants is the closure: one row per (ancestor, descendant) pair, anchored
   at every category paired with itself so a leaf still rolls up to its own
   direct count. Two non-recursive CTEs then do the arithmetic, and the result
   joins back to CategoryTree for presentation. Poetry appears with zeroes --
   an absence the direct GROUP BY could not have reported at all.
   ============================================================================ */
PRINT '=== 2.  Descendant roll-up — direct vs inherited quote counts ===';

WITH CategoryTree AS
(
    SELECT
        c.CategoryId,
        c.Name,
        Depth = 0,
        Path  = CAST(c.Name AS nvarchar(400))
    FROM app.Category AS c
    WHERE c.ParentCategoryId IS NULL

    UNION ALL

    SELECT
        c.CategoryId,
        c.Name,
        Depth = p.Depth + 1,
        Path  = CAST(p.Path + N' > ' + c.Name AS nvarchar(400))
    FROM app.Category  AS c
    INNER JOIN CategoryTree AS p ON p.CategoryId = c.ParentCategoryId
),
Descendants AS
(
    SELECT
        AncestorId   = c.CategoryId,
        DescendantId = c.CategoryId
    FROM app.Category AS c

    UNION ALL

    SELECT
        d.AncestorId,
        DescendantId = c.CategoryId
    FROM app.Category AS c
    INNER JOIN Descendants AS d ON d.DescendantId = c.ParentCategoryId
),
DirectCounts AS
(
    SELECT
        q.CategoryId,
        DirectQuotes = COUNT(*)
    FROM app.Quote AS q
    WHERE q.IsDeleted  = 0
      AND q.CategoryId IS NOT NULL
    GROUP BY q.CategoryId
),
RolledUpCounts AS
(
    SELECT
        d.AncestorId,
        TotalQuotes   = COUNT(q.QuoteId),
        TotalAuthors  = COUNT(DISTINCT q.AuthorId)
    FROM Descendants AS d
    LEFT JOIN app.Quote AS q
           ON q.CategoryId = d.DescendantId
          AND q.IsDeleted  = 0
    GROUP BY d.AncestorId
)
SELECT
    t.Depth,
    Category      = REPLICATE(N'  ', t.Depth) + t.Name,
    DirectQuotes  = COALESCE(dc.DirectQuotes, 0),
    TotalQuotes   = r.TotalQuotes,
    TotalAuthors  = r.TotalAuthors
FROM CategoryTree AS t
INNER JOIN RolledUpCounts AS r  ON r.AncestorId  = t.CategoryId
LEFT  JOIN DirectCounts   AS dc ON dc.CategoryId = t.CategoryId
ORDER BY t.Path
OPTION (MAXRECURSION 50);
GO

/* ============================================================================
   3.  Recursing upward — the ancestry of one node.
   ----------------------------------------------------------------------------
   The same mechanism with the join reversed: the anchor is the node itself and
   each step climbs to the parent. Useful for breadcrumbs, and for answering
   "which root does this belong to" without storing a denormalised root column
   that has to be maintained.
   ============================================================================ */
PRINT '=== 3.  Ancestors of Concurrency, root first ===';

WITH Ancestry AS
(
    SELECT
        c.CategoryId,
        c.Name,
        c.ParentCategoryId,
        StepsUp = 0
    FROM app.Category AS c
    WHERE c.Name = N'Concurrency'

    UNION ALL

    SELECT
        parent.CategoryId,
        parent.Name,
        parent.ParentCategoryId,
        StepsUp = child.StepsUp + 1
    FROM app.Category AS parent
    INNER JOIN Ancestry AS child ON child.ParentCategoryId = parent.CategoryId
)
SELECT
    a.StepsUp,
    a.CategoryId,
    a.Name
FROM Ancestry AS a
ORDER BY a.StepsUp DESC
OPTION (MAXRECURSION 50);
GO

/* ============================================================================
   4.  Cycles, and why MAXRECURSION is a backstop rather than a guard.
   ----------------------------------------------------------------------------
   The foreign key on Category stops a row being its own parent, and nothing
   more. A -> B -> A satisfies every constraint in the schema and turns the
   traversal above into an infinite loop. SQL Server defaults to MAXRECURSION
   100 and raises error 530 when it is hit, which stops the server melting but
   fails the query -- and OPTION (MAXRECURSION 0) removes even that.

   The real guard is to carry the visited path and refuse to re-enter it. Below,
   a temp copy of the tree is given a genuine cycle (Philosophy is made a child
   of its own grandchild) and traversed twice: unguarded until MAXRECURSION cuts
   it off, then guarded, which terminates on its own and reports the edge it
   refused to follow.

   Delimiters around each id in the path matter. Without them, LIKE '%1%' also
   matches ids 12 and 21.
   ============================================================================ */
PRINT '=== 4.  Cycle handling ===';

DROP TABLE IF EXISTS #CyclicCategory;

SELECT
    c.CategoryId,
    c.Name,
    c.ParentCategoryId
INTO #CyclicCategory
FROM app.Category AS c;

-- Philosophy (1) becomes a child of Practical Stoicism (12), which is already
-- its grandchild. 1 -> 4 -> 12 -> 1 is now a cycle with no constraint violated.
UPDATE #CyclicCategory
SET ParentCategoryId = 12
WHERE CategoryId = 1;

PRINT '--- unguarded: MAXRECURSION stops it, with an error ---';
BEGIN TRY
    ;WITH Unguarded AS
    (
        SELECT c.CategoryId, c.Name, Depth = 0
        FROM #CyclicCategory AS c
        WHERE c.CategoryId = 4          -- Stoicism, inside the cycle

        UNION ALL

        SELECT c.CategoryId, c.Name, Depth = p.Depth + 1
        FROM #CyclicCategory AS c
        INNER JOIN Unguarded AS p ON p.CategoryId = c.ParentCategoryId
    )
    SELECT RowsBeforeCutoff = COUNT(*) FROM Unguarded
    OPTION (MAXRECURSION 20);
END TRY
BEGIN CATCH
    SELECT
        ErrorNumber  = ERROR_NUMBER(),
        ErrorMessage = LEFT(ERROR_MESSAGE(), 120);
END CATCH;

PRINT '--- guarded: the traversal terminates on its own ---';
;WITH Guarded AS
(
    SELECT
        c.CategoryId,
        c.Name,
        Depth       = 0,
        VisitedPath = CAST(N'|' + CAST(c.CategoryId AS nvarchar(10)) + N'|' AS nvarchar(400))
    FROM #CyclicCategory AS c
    WHERE c.CategoryId = 4

    UNION ALL

    SELECT
        c.CategoryId,
        c.Name,
        Depth       = p.Depth + 1,
        VisitedPath = CAST(p.VisitedPath + CAST(c.CategoryId AS nvarchar(10)) + N'|' AS nvarchar(400))
    FROM #CyclicCategory AS c
    INNER JOIN Guarded AS p ON p.CategoryId = c.ParentCategoryId
    WHERE p.VisitedPath NOT LIKE N'%|' + CAST(c.CategoryId AS nvarchar(10)) + N'|%'
)
SELECT
    g.Depth,
    g.CategoryId,
    g.Name,
    g.VisitedPath
FROM Guarded AS g
ORDER BY g.Depth, g.CategoryId
OPTION (MAXRECURSION 50);

PRINT '--- the edge the guard declined to follow ---';
;WITH Guarded AS
(
    SELECT
        c.CategoryId,
        c.Name,
        VisitedPath = CAST(N'|' + CAST(c.CategoryId AS nvarchar(10)) + N'|' AS nvarchar(400))
    FROM #CyclicCategory AS c
    WHERE c.CategoryId = 4

    UNION ALL

    SELECT
        c.CategoryId,
        c.Name,
        VisitedPath = CAST(p.VisitedPath + CAST(c.CategoryId AS nvarchar(10)) + N'|' AS nvarchar(400))
    FROM #CyclicCategory AS c
    INNER JOIN Guarded AS p ON p.CategoryId = c.ParentCategoryId
    WHERE p.VisitedPath NOT LIKE N'%|' + CAST(c.CategoryId AS nvarchar(10)) + N'|%'
)
SELECT DISTINCT
    DeclinedEdge = g.Name + N' -> ' + child.Name,
    ReachedVia   = g.VisitedPath
FROM Guarded AS g
INNER JOIN #CyclicCategory AS child ON child.ParentCategoryId = g.CategoryId
WHERE g.VisitedPath LIKE N'%|' + CAST(child.CategoryId AS nvarchar(10)) + N'|%'
OPTION (MAXRECURSION 50);

DROP TABLE IF EXISTS #CyclicCategory;
GO

/* ============================================================================
   5.  Non-recursive CTEs — the common case.
   ----------------------------------------------------------------------------
   Nothing here recurses. The CTEs exist because the alternative is three levels
   of nested derived tables, where the reader has to work inside-out and the
   middle step has no name. Each stage is named for what it produces, and the
   final SELECT reads as a sentence.

   The question: for each root category, how many live quotes and distinct
   authors sit under it, and which tag dominates it.

   The per-root totals do not sum to the live quote count, and should not: a
   quote with a NULL CategoryId belongs to no root, so the INNER JOIN in
   LiveQuotes excludes it by design rather than by accident.
   ============================================================================ */
PRINT '=== 5.  Non-recursive CTEs as named stages ===';

WITH RootOf AS
(
    -- one recursion is unavoidable to attribute a leaf to its root, so it is
    -- confined to this stage and the rest of the query is flat
    SELECT
        CategoryId = c.CategoryId,
        RootId     = c.CategoryId,
        RootName   = CAST(c.Name AS nvarchar(80))
    FROM app.Category AS c
    WHERE c.ParentCategoryId IS NULL

    UNION ALL

    SELECT
        CategoryId = c.CategoryId,
        RootId     = p.RootId,
        RootName   = p.RootName
    FROM app.Category AS c
    INNER JOIN RootOf AS p ON p.CategoryId = c.ParentCategoryId
),
LiveQuotes AS
(
    SELECT
        r.RootId,
        r.RootName,
        q.QuoteId,
        q.AuthorId
    FROM app.Quote AS q
    INNER JOIN RootOf AS r ON r.CategoryId = q.CategoryId
    WHERE q.IsDeleted = 0
),
RootTotals AS
(
    SELECT
        lq.RootId,
        lq.RootName,
        Quotes  = COUNT(*),
        Authors = COUNT(DISTINCT lq.AuthorId)
    FROM LiveQuotes AS lq
    GROUP BY lq.RootId, lq.RootName
),
TagUsage AS
(
    SELECT
        lq.RootId,
        TagName = t.Name,
        Uses    = COUNT(*),
        TagRank = ROW_NUMBER() OVER (
                      PARTITION BY lq.RootId
                      ORDER BY     COUNT(*) DESC, t.Name)
    FROM LiveQuotes     AS lq
    INNER JOIN app.QuoteTag AS qt ON qt.QuoteId = lq.QuoteId
    INNER JOIN app.Tag      AS t  ON t.TagId    = qt.TagId
    GROUP BY lq.RootId, t.Name
)
SELECT
    RootCategory  = rt.RootName,
    Quotes        = rt.Quotes,
    Authors       = rt.Authors,
    DominantTag   = tu.TagName,
    DominantUses  = tu.Uses
FROM RootTotals AS rt
LEFT JOIN TagUsage AS tu
       ON tu.RootId  = rt.RootId
      AND tu.TagRank = 1
ORDER BY rt.Quotes DESC, rt.RootName
OPTION (MAXRECURSION 50);
GO
