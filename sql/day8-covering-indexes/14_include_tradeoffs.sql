/* ============================================================================
   Day 8, piece 2 — what INCLUDE actually costs, and what it stops covering
   ----------------------------------------------------------------------------
   13_covering_index_lab.sql answers the exercise. This is the part that decides
   whether the answer survives contact with a codebase.

     1.  INCLUDE against putting the same columns in the key, measured.
     2.  The one-column edit that silently un-covers a covering index.
     3.  RID Lookup -- the same operator on a heap, under a different name.
     4.  Where widening stops paying, in pages.
     5.  Cleanup, back to the three indexes piece 1's README documents.

   Prerequisite: 13_covering_index_lab.sql, which creates both UserId indexes,
   perf.CoverLog and perf.MeasureCovering.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF  OBJECT_ID('perf.CoverLog')        IS NULL
 OR OBJECT_ID('perf.MeasureCovering') IS NULL
 OR NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('perf.QuoteView')
                  AND name = 'IX_QuoteView_UserId_ViewedAt_Covering')
BEGIN
    THROW 50812, 'Run 13_covering_index_lab.sql before this script.', 1;
END
GO

/* ============================================================================
   1.  INCLUDE against key columns.
   ----------------------------------------------------------------------------
   Both of these cover perf.Q6 completely. They differ only in where the three
   extra columns live:

       IX_QuoteView_UserId_ViewedAt_Covering
           key     (UserId, ViewedAt DESC, ViewId DESC)
           include (DeviceType, CountryCode, DwellSeconds)

       IX_Cover_KeyedColumns
           key     (UserId, ViewedAt DESC, ViewId DESC,
                    DeviceType, CountryCode, DwellSeconds)

   A B-tree carries its key in every level. Included columns exist only in the
   leaf. So the leaf levels of these two indexes hold the same data and their
   avg_record_size_in_bytes at index_level 0 should agree almost exactly, while
   the non-leaf levels should not: index_level 1 of the keyed version has to
   carry DeviceType, CountryCode and DwellSeconds in every row it stores, for no
   benefit this query can use.

   avg_record_size_in_bytes at index_level 1 is the measurement, and it is the
   only clean one here. Do not read the non-leaf page_count as the comparison:
   at 100,000 rows the upper levels are a handful of pages and their
   avg_page_space_used_in_percent varies enough between builds to swamp the
   record-size effect, so the index with the smaller rows can easily end up
   holding more pages. The row width is the structural fact and the thing that
   scales; the page count at this size is noise.

   The differences that are not about size, and that this fixture cannot show:
   key columns are ordered, so they can be seeked and can satisfy an ORDER BY;
   they count against the 1,700-byte, 32-column index key limit; and they must be
   of a type a key allows. Included columns are unordered and can only be
   projected, do not count against the key limit at all, and may be types a key
   forbids -- nvarchar(max), varbinary(max), xml. That last one is the case where
   INCLUDE is not an optimisation but the only option.
   ============================================================================ */
PRINT '=== 1.  Creating the all-in-the-key variant for comparison ===';

CREATE NONCLUSTERED INDEX IX_Cover_KeyedColumns
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC, DeviceType, CountryCode, DwellSeconds);
GO

PRINT '--- 1a.  Per level: the leaf agrees, the non-leaf row width does not ---';

SELECT
    IndexName      = i.name,
    IndexLevel     = ips.index_level,
    LevelKind      = CASE WHEN ips.index_level = 0 THEN 'leaf' ELSE 'non-leaf' END,
    ips.page_count,
    ips.record_count,
    AvgRecordBytes = CONVERT(decimal(8,2), ips.avg_record_size_in_bytes),
    PageFullnessPct = CONVERT(decimal(5,2), ips.avg_page_space_used_in_percent)
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL, 'DETAILED') AS ips
INNER JOIN sys.indexes AS i
        ON i.object_id = ips.object_id
       AND i.index_id  = ips.index_id
WHERE i.name IN ('IX_QuoteView_UserId_ViewedAt_Covering', 'IX_Cover_KeyedColumns')
ORDER BY i.name, ips.index_level;
GO

/* ============================================================================
   2.  The edit that un-covers a covering index.
   ----------------------------------------------------------------------------
   perf.Q7 is perf.Q6 with one column added to the projection: Referrer, for a
   distinct-referrer count. Nothing else changes -- same predicate, same
   grouping, same ordering.

   The covering index is still there. It is still the right index. It no longer
   covers, so the key lookups come straight back, one per matched row.

   This is why "add a covering index" is not a fix you can walk away from. The
   index encodes a projection, and a projection is the easiest thing in a query
   for someone to widen six months later while adding a column to a dashboard.
   Nothing fails, nothing warns, and the plan quietly reverts.
   ============================================================================ */
CREATE OR ALTER PROCEDURE perf.Q7_AccountActivityMixPlusReferrer
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        v.DeviceType,
        v.CountryCode,
        Views             = COUNT(*),
        AvgDwell          = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0)),
        DistinctReferrers = COUNT(DISTINCT v.Referrer)
    FROM perf.QuoteView AS v
    WHERE v.UserId IN (137, 842, 1699, 2001, 2444)
    GROUP BY v.DeviceType, v.CountryCode
    ORDER BY Views DESC, v.DeviceType, v.CountryCode
    OPTION (MAXDOP 1);
END
GO

PRINT '';
PRINT '=== 2.  One extra column in the SELECT list, with the covering index in place ===';
GO

SET STATISTICS IO ON;
GO

EXEC perf.Q7_AccountActivityMixPlusReferrer;
GO

SET STATISTICS IO OFF;
GO

EXEC perf.MeasureCovering 'un-covered', 'Q7 same query plus Referrer', 'perf.Q7_AccountActivityMixPlusReferrer';
GO

/* ============================================================================
   3.  RID Lookup — the same thing on a heap.
   ----------------------------------------------------------------------------
   A non-clustered index stores a row locator, and what that locator is depends
   on the table. On a clustered table it is a copy of the clustering key, and
   following it is a Clustered Index Seek marked LOOKUP. On a heap it is an
   8-byte physical row id, and following it is a RID Lookup -- which, unlike the
   clustered case, does appear in a text plan under that name.

   Same concept, same fix, different operator to look for. Worth seeing once,
   because a plan full of RID Lookups reads as unfamiliar to anyone who has only
   ever met the clustered form.

   Twenty thousand rows cloned out of perf.QuoteView, indexed on CountryCode,
   queried for the rare value so the seek is never in doubt.
   ============================================================================ */
DROP TABLE IF EXISTS perf.CoverHeap;
GO

SELECT TOP (20000)
    ViewId,
    QuoteId,
    ViewedAt,
    CountryCode,
    DeviceType,
    DwellSeconds
INTO perf.CoverHeap
FROM perf.QuoteView
ORDER BY ViewId;
GO

CREATE NONCLUSTERED INDEX IX_CoverHeap_CountryCode
    ON perf.CoverHeap (CountryCode);
GO

PRINT '';
PRINT '=== 3a.  A heap, a non-covering index, and a RID Lookup ===';
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

SELECT
    v.ViewId,
    v.ViewedAt,
    v.DeviceType,
    v.DwellSeconds
FROM perf.CoverHeap AS v
WHERE v.CountryCode = 'MT'
ORDER BY v.ViewId
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS PROFILE OFF;
GO

PRINT '--- 3b.  The same query once the index covers it ---';

/* Every projected column has to be named. That is a real asymmetry rather than
   an oversight: covering an index on a clustered table gets the clustering key
   thrown in, because it is already in the leaf as the row locator, so ViewedAt
   and ViewId cost nothing to project. A heap has no clustering key, so nothing
   comes free. */
CREATE NONCLUSTERED INDEX IX_CoverHeap_CountryCode_Covering
    ON perf.CoverHeap (CountryCode)
    INCLUDE (ViewId, ViewedAt, DeviceType, DwellSeconds);
GO

SET STATISTICS PROFILE ON;
GO

SELECT
    v.ViewId,
    v.ViewedAt,
    v.DeviceType,
    v.DwellSeconds
FROM perf.CoverHeap AS v
WHERE v.CountryCode = 'MT'
ORDER BY v.ViewId
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS PROFILE OFF;
SET STATISTICS IO OFF;
GO

/* ============================================================================
   4.  Where widening stops paying.
   ----------------------------------------------------------------------------
   The obvious response to section 2 is to include Referrer as well, and while we
   are there, UserAgent. Both are varchar(120). Both get copied into every one of
   100,000 leaf rows.

   So: build that index, confirm it does cover Q7, and then price it beside the
   others. The reads saved are worth having. Whether they are worth this many
   pages is a judgement, and the point of measuring is to make it a judgement
   rather than a reflex.
   ============================================================================ */
PRINT '';
PRINT '=== 4.  The fat covering index: five INCLUDEs, two of them varchar(120) ===';

CREATE NONCLUSTERED INDEX IX_Cover_Fat
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DeviceType, CountryCode, DwellSeconds, Referrer, UserAgent);
GO

SET STATISTICS IO ON;
GO

EXEC perf.Q7_AccountActivityMixPlusReferrer;
GO

SET STATISTICS IO OFF;
GO

EXEC perf.MeasureCovering 're-covered', 'Q7 against the fat index', 'perf.Q7_AccountActivityMixPlusReferrer';
GO

PRINT '--- 4a.  Every non-clustered index on the table, priced ---';

/* Piece 1's two indexes are in this list as well, deliberately: they are the
   reference points for what a narrow index costs on this table. */
SELECT
    IndexName    = i.name,
    KeyColumns   = k.Cols,
    IncludedCols = ISNULL(inc.Cols, '-'),
    UsedPages    = ps.used_page_count,
    UsedKB       = ps.used_page_count * 8,
    LeafPages    = ips.page_count,
    AvgLeafBytes = CONVERT(decimal(8,2), ips.avg_record_size_in_bytes)
FROM sys.indexes AS i
INNER JOIN sys.dm_db_partition_stats AS ps
        ON ps.object_id = i.object_id AND ps.index_id = i.index_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), i.object_id, i.index_id, NULL, 'DETAILED') AS ips
OUTER APPLY (
    SELECT Cols = STRING_AGG(
                      CONVERT(nvarchar(max),
                          c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END),
                      ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
    FROM sys.index_columns AS ic
    INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
      AND ic.is_included_column = 0
) AS k
OUTER APPLY (
    SELECT Cols = STRING_AGG(CONVERT(nvarchar(max), c.name), ', ')
                      WITHIN GROUP (ORDER BY c.name)
    FROM sys.index_columns AS ic
    INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
      AND ic.is_included_column = 1
) AS inc
WHERE i.object_id     = OBJECT_ID('perf.QuoteView')
  AND i.type_desc     = 'NONCLUSTERED'
  AND ips.index_level = 0
ORDER BY ps.used_page_count;
GO

PRINT '--- 4b.  Reads and lookups across every stage of both scripts ---';

SELECT
    Stage,
    QueryName,
    LogicalReads,
    KeyLookups
FROM perf.CoverLog
ORDER BY Seq;
GO

/* ============================================================================
   5.  Cleanup.
   ----------------------------------------------------------------------------
   Everything this piece created comes out, leaving perf.QuoteView with the one
   clustered and two non-clustered indexes that piece 1's README documents and
   that 11_actual_plans.sql captured its plans against.

   That is a decision about a shared fixture rather than a recommendation. In
   production IX_QuoteView_UserId_ViewedAt_Covering would *replace*
   IX_QuoteView_UserId_ViewedAt, not sit beside it: the keys are identical
   character for character, so the narrow one answers nothing the wide one
   cannot, and keeping both means paying for two index writes on every insert to
   serve one read path. Both are dropped here because two submitted pieces share
   one database, and the earlier one's captured evidence has to stay
   reproducible.
   ============================================================================ */
PRINT '';
PRINT '=== 5.  Dropping everything this piece created ===';

DROP INDEX IF EXISTS IX_Cover_Fat                           ON perf.QuoteView;
DROP INDEX IF EXISTS IX_Cover_KeyedColumns                  ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt_Covering  ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt           ON perf.QuoteView;
GO

DROP TABLE IF EXISTS perf.CoverHeap;
GO

PRINT '=== 5b.  Final state -- the three indexes from piece 1, unchanged ===';

EXEC perf.ShowIndexes;
GO

PRINT 'Covering-index trade-offs complete.';
GO
