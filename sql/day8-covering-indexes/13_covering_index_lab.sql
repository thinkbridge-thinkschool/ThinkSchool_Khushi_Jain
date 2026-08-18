/* ============================================================================
   Day 8, piece 2 — covering indexes and included columns
   ----------------------------------------------------------------------------
   The exercise: take a query doing a key lookup, add an index with INCLUDEd
   columns to eliminate it, and prove it from the plan.

   Getting a key lookup to happen at all on this table is the part that takes
   design, and it is worth saying why before the SQL starts.

   A key lookup is only ever the optimiser's choice inside a window. Too few
   matching rows and there is nothing to measure; too many and a scan of the
   clustered index is cheaper, so the index is abandoned and there is no lookup
   to eliminate. On perf.QuoteView that upper edge sits near 640 rows: the
   clustered index is 1,948 pages and three levels deep, so a lookup costs about
   three reads and 1932/3 is where the two plans cross. Piece 1 section 7a
   measured the far side of that cliff at 88x.

   Two further escapes have to be closed, and both of them are properties of the
   clustered key rather than of the query:

     * No TOP with an ORDER BY on ViewedAt. The clustered index is ordered on
       ViewedAt and can be read backwards, so a TOP-N query terminates early --
       it reads a few hundred pages and stops, which beats a few hundred random
       lookups. Piece 1's Q2 showed this happening for free at stage 1.
     * No bound on ViewedAt. ViewedAt is the leading clustered column, so any
       time-bounded predicate can be served by a clustered range seek, and again
       the lookup plan loses.

   So the predicate has to be an equality on a column that is neither the
   clustered key nor correlated with it, selective enough to stay under the
   cliff, with no TOP and no time bound. UserId is that column: 2,500 accounts,
   about forty views each, drawn from an independent hash slice so it has no
   relationship to insert order.

   Prerequisite: 09_build_dataset.sql for the table, and 10_index_lab.sql for
   the perf.ShowIndexes helper. Both indexes this script uses are its own, and
   14_include_tradeoffs.sql drops them, so perf.QuoteView ends the run in the
   three-index state piece 1's README documents.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   0.  Preconditions, and back to the "before" state.
   ============================================================================ */
IF  OBJECT_ID('perf.QuoteView')   IS NULL
 OR OBJECT_ID('perf.ShowIndexes') IS NULL
BEGIN
    THROW 50811, 'perf.QuoteView or perf.ShowIndexes is missing. Run 09_build_dataset.sql then 10_index_lab.sql first.', 1;
END
GO

/* Re-runnable: a "before" measurement taken with the covering index already in
   place is not a before measurement. */
DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt_Covering ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt          ON perf.QuoteView;
GO

DROP TABLE IF EXISTS perf.CoverLog;
GO

CREATE TABLE perf.CoverLog
(
    Seq          int IDENTITY(1,1) NOT NULL,
    Stage        varchar(30)       NOT NULL,
    QueryName    varchar(40)       NOT NULL,
    LogicalReads bigint            NOT NULL,
    KeyLookups   bigint            NOT NULL,
    CONSTRAINT PK_CoverLog PRIMARY KEY CLUSTERED (Seq)
);
GO

/* ============================================================================
   1.  The index the query starts with.
   ----------------------------------------------------------------------------
   CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt
       ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC);

   This is not a strawman built to fail. It is the index anyone would write for
   "this account's views, newest first" -- equality on the account, then the
   timeline in the direction it gets read -- and it is exactly the shape piece 1
   chose for QuoteId, for exactly the same reasons.

   What it does not have is a projection. It finds rows beautifully and can
   produce almost nothing about them, and that is the ordinary way a covering
   problem arrives: not from a bad index, but from a good index and a SELECT list
   that grew after it was designed.
   ============================================================================ */
PRINT '=== 1.  The starting index: a real one, with no INCLUDE list ===';

CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC);
GO

EXEC perf.ShowIndexes;
GO

PRINT '--- 1a.  The arithmetic that decides whether a lookup plan is viable ---';

/* Printed rather than asserted. MatchedRows is how many lookups the before plan
   has to perform; CliffAtRoughly is where seek-plus-lookup stops being cheaper
   than reading the clustered index end to end. The first needs to be comfortably
   under the second or this exercise has nothing to show. */
SELECT
    MatchedRows      = COUNT(*),
    ClusteredPages   = (SELECT ps.used_page_count
                        FROM sys.dm_db_partition_stats AS ps
                        WHERE ps.object_id = OBJECT_ID('perf.QuoteView')
                          AND ps.index_id  = 1),
    CliffAtRoughly   = (SELECT ps.used_page_count / 3
                        FROM sys.dm_db_partition_stats AS ps
                        WHERE ps.object_id = OBJECT_ID('perf.QuoteView')
                          AND ps.index_id  = 1)
FROM perf.QuoteView
WHERE UserId IN (137, 842, 1699, 2001, 2444);
GO

/* ============================================================================
   2.  The query, and the harness that measures it.
   ----------------------------------------------------------------------------
   An account-activity summary: for five accounts, how their views break down by
   device and country. The predicate and the ordering are served by the index
   above. DeviceType, CountryCode and DwellSeconds are not in it, and each is a
   reason to go back to the table.

   No TOP and no bound on ViewedAt, for the reasons in the header. The GROUP BY
   also means the whole matched set has to be read -- there is no way for any
   plan to stop early.
   ============================================================================ */
CREATE OR ALTER PROCEDURE perf.Q6_AccountActivityMix
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        v.DeviceType,
        v.CountryCode,
        Views    = COUNT(*),
        AvgDwell = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
    FROM perf.QuoteView AS v
    WHERE v.UserId IN (137, 842, 1699, 2001, 2444)
    GROUP BY v.DeviceType, v.CountryCode
    ORDER BY Views DESC, v.DeviceType, v.CountryCode
    OPTION (MAXDOP 1);
END
GO

/* --- The harness. ------------------------------------------------------------
   Two numbers per stage: logical reads for the statement, and the count of
   singleton lookups served by the clustered index, which is literally the
   number of key lookups the plan performed. The second is what turns "the
   lookup is gone" from a reading of a plan diagram into an integer.

   It primes before it measures, and that matters. The read counter is per
   request, so the catalog pages read to compile a plan land inside the
   measurement -- twenty to forty of them, which is noise against a lookup storm
   and is most of the number against a covering seek that reads four pages.
   Creating or dropping an index invalidates every cached plan for the table, so
   the priming pass recompiles against the current index set and the measured
   pass reuses that plan. That is also why perf.Q6 carries MAXDOP 1 but not
   RECOMPILE: forcing a recompile would put the compile cost back inside every
   measurement.

   Reads are snapshotted last before the call and first after it, so the only
   work between the two readings is the query itself.
   --------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE perf.MeasureCovering
    @Stage     varchar(30),
    @QueryName varchar(40),
    @ProcName  sysname
AS
BEGIN
    SET NOCOUNT ON;

    EXEC @ProcName;                                  -- prime: compile, discard

    DECLARE @lookups0 bigint =
        (SELECT os.singleton_lookup_count
         FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), 1, NULL) AS os);
    DECLARE @reads0 bigint =
        (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);

    EXEC @ProcName;                                  -- measured

    DECLARE @reads1 bigint =
        (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);
    DECLARE @lookups1 bigint =
        (SELECT os.singleton_lookup_count
         FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), 1, NULL) AS os);

    INSERT perf.CoverLog (Stage, QueryName, LogicalReads, KeyLookups)
    VALUES (@Stage, @QueryName, @reads1 - @reads0, @lookups1 - @lookups0);
END
GO

/* ============================================================================
   3.  BEFORE — the query with the key lookup.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### BEFORE - IX_QuoteView_UserId_ViewedAt, not covering   ###';
PRINT '############################################################';
GO

PRINT '--- 3a.  SET STATISTICS IO ON, one execution. This is the record. ---';
GO

SET STATISTICS IO ON;
GO

EXEC perf.Q6_AccountActivityMix;
GO

SET STATISTICS IO OFF;
GO

PRINT '--- 3b.  Reads and key-lookup count, into perf.CoverLog ---';

EXEC perf.MeasureCovering 'before', 'Q6 account activity mix', 'perf.Q6_AccountActivityMix';
GO

/* ============================================================================
   4.  The BEFORE plan.
   ----------------------------------------------------------------------------
   Read the tree bottom up. Expect an Index Seek on IX_QuoteView_UserId_ViewedAt
   feeding a Nested Loops whose inner side is a Clustered Index Seek on
   CIX_QuoteView_ViewedAt marked LOOKUP.

   A key lookup does not appear in a text plan as the words "Key Lookup" -- that
   is the label graphical plans use. Here it is a Clustered Index Seek whose
   Argument carries the keyword LOOKUP and whose seek predicate is the whole
   clustering key:

       Clustered Index Seek(OBJECT:(...CIX_QuoteView_ViewedAt...),
           SEEK:([v].[ViewedAt]=... AND [v].[ViewId]=...) LOOKUP ORDERED FORWARD)

   Its Executes column is the number of round trips, and it should equal
   MatchedRows from section 1a. That is the number this piece drives to zero.
   ============================================================================ */
PRINT '';
PRINT '=== 4.  BEFORE plan -- look for LOOKUP, and Executes = the matched rows ===';
GO

SET STATISTICS PROFILE ON;
GO

EXEC perf.Q6_AccountActivityMix;
GO

SET STATISTICS PROFILE OFF;
GO

/* ============================================================================
   5.  The index with INCLUDE.
   ----------------------------------------------------------------------------
   CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt_Covering
       ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
       INCLUDE (DeviceType, CountryCode, DwellSeconds);

   The key is unchanged, character for character. Only the leaf gets wider. That
   is the whole intervention: the seek and the ordering were already right, and
   the query was leaving the index for three columns it could have been handed.

   All three go in INCLUDE rather than in the key because the query neither
   searches nor sorts by them -- it groups by two of them and averages the third,
   after the rows have already been found. Included columns exist only in the
   leaf level, so they widen the leaf and leave the B-tree's depth and its
   intermediate levels alone. 14_include_tradeoffs.sql section 1 measures that
   against the version that puts the same three columns in the key.
   ============================================================================ */
PRINT '';
PRINT '=== 5.  Creating the covering index ===';

CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt_Covering
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DeviceType, CountryCode, DwellSeconds);
GO

EXEC perf.ShowIndexes;
GO

/* ============================================================================
   6.  AFTER — the identical query, unchanged.
   ----------------------------------------------------------------------------
   Not one character of perf.Q6 changed. The whole difference is the DDL above,
   which is the point worth making to anyone who reaches for a query rewrite
   first.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### AFTER - the same query, against the covering index    ###';
PRINT '############################################################';
GO

PRINT '--- 6a.  SET STATISTICS IO ON, one execution ---';
GO

SET STATISTICS IO ON;
GO

EXEC perf.Q6_AccountActivityMix;
GO

SET STATISTICS IO OFF;
GO

PRINT '--- 6b.  Reads and key-lookup count ---';

EXEC perf.MeasureCovering 'after', 'Q6 account activity mix', 'perf.Q6_AccountActivityMix';
GO

/* ============================================================================
   7.  The AFTER plan.
   ----------------------------------------------------------------------------
   Expect the Nested Loops and the LOOKUP seek to be absent entirely -- not
   cheaper, absent. What remains is an Index Seek on the covering index and the
   aggregate above it. CIX_QuoteView_ViewedAt is not touched at all, which is
   what "served entirely from the index" means.
   ============================================================================ */
PRINT '';
PRINT '=== 7.  AFTER plan -- no Nested Loops, no LOOKUP, no clustered index ===';
GO

SET STATISTICS PROFILE ON;
GO

EXEC perf.Q6_AccountActivityMix;
GO

SET STATISTICS PROFILE OFF;
GO

/* ============================================================================
   8.  The delta.
   ============================================================================ */
PRINT '';
PRINT '=== 8a.  Logical reads and key lookups, before and after ===';

SELECT
    QueryName,
    BeforeReads   = MAX(CASE WHEN Stage = 'before' THEN LogicalReads END),
    AfterReads    = MAX(CASE WHEN Stage = 'after'  THEN LogicalReads END),
    ReadsSaved    = MAX(CASE WHEN Stage = 'before' THEN LogicalReads END)
                  - MAX(CASE WHEN Stage = 'after'  THEN LogicalReads END),
    BeforeLookups = MAX(CASE WHEN Stage = 'before' THEN KeyLookups END),
    AfterLookups  = MAX(CASE WHEN Stage = 'after'  THEN KeyLookups END),
    TimesCheaper  = CONVERT(decimal(10,2),
                        MAX(CASE WHEN Stage = 'before' THEN LogicalReads END) * 1.0
                      / NULLIF(MAX(CASE WHEN Stage = 'after' THEN LogicalReads END), 0))
FROM perf.CoverLog
GROUP BY QueryName
ORDER BY QueryName;
GO

PRINT '=== 8b.  What the three INCLUDE columns cost in space ===';

/* The honest other half. The two indexes have identical keys and identical row
   counts, so the difference in avg_record_size_in_bytes at index_level 0 is
   exactly what DeviceType, CountryCode and DwellSeconds add to every leaf row,
   and the difference in page_count is what that costs on the storage bill. */
SELECT
    IndexName          = i.name,
    IndexLevel         = ips.index_level,
    ips.page_count,
    ips.record_count,
    AvgRecordBytes     = CONVERT(decimal(8,2), ips.avg_record_size_in_bytes),
    AvgPageFullnessPct = CONVERT(decimal(5,2), ips.avg_page_space_used_in_percent)
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL, 'DETAILED') AS ips
INNER JOIN sys.indexes AS i
        ON i.object_id = ips.object_id
       AND i.index_id  = ips.index_id
WHERE i.name IN ('IX_QuoteView_UserId_ViewedAt', 'IX_QuoteView_UserId_ViewedAt_Covering')
ORDER BY i.name, ips.index_level;
GO

PRINT 'Covering index lab complete. Run 14_include_tradeoffs.sql next.';
GO
