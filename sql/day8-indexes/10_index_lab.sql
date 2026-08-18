/* ============================================================================
   Day 8 — clustered vs non-clustered, measured
   ----------------------------------------------------------------------------
   The exercise: one clustered index, two non-clustered indexes, a query that
   uses each, and the logical reads before and after each one.

   The method is a staircase. Five queries are written once, as procedures, and
   then run in full at every step:

       stage 0   heap                             nothing can help any query
       stage 1   + UNIQUE CLUSTERED               Q1
       stage 2   + IX_..._QuoteId_ViewedAt        Q2, Q3
       stage 3   + IX_..._CountryCode             Q4 -- and pointedly not Q5

   Running all five at every stage costs nothing and answers the question the
   exercise does not ask: what an index does to the queries it was *not* built
   for. Three of the twenty numbers below get worse, and the reason each one
   gets worse is the interesting part.

   Logical reads, not elapsed time. A logical read is one 8KB page handed to the
   query, whether it came from the buffer pool or from disk, so the count is a
   property of the query and the indexes and nothing else. Elapsed time on the
   same query varies with cache state, other containers on the machine and what
   the laptop's power governor is doing; it is the number that feels like
   evidence and is not.

   Every measured query carries OPTION (MAXDOP 1, RECOMPILE). MAXDOP 1 because
   a plan that goes parallel on one machine and serial on another reports
   different work for the same query, and RECOMPILE so each stage is planned
   against the indexes that exist at that stage rather than reusing a cached
   plan from the stage before.

   Runtime: under a minute, most of it section 7's deliberately terrible query.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

SET STATISTICS IO ON;
GO

/* ============================================================================
   0.  Back to a heap.
   ----------------------------------------------------------------------------
   This script has to be re-runnable, and a "before" measurement taken on a
   table that still has last run's indexes on it is not a before measurement.

   The order matters and is worth stating, because it is a real operational
   cost rather than a formality. A non-clustered index stores a row locator for
   every row: on a heap that is an 8-byte physical RID, on a clustered table it
   is a copy of the clustering key. So adding or dropping a clustered index
   rewrites every non-clustered index on the table. Dropping the non-clustered
   ones first means one rebuild instead of two, and it is the same reason
   stage 1 below creates the clustered index before either non-clustered one.
   ============================================================================ */
PRINT '=== 0.  Resetting perf.QuoteView to a heap ===';

DROP INDEX IF EXISTS IX_QuoteView_CountryCode          ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_CountryCode_Covering ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_QuoteId_ViewedAt     ON perf.QuoteView;
DROP INDEX IF EXISTS CIX_QuoteView_ViewedAt            ON perf.QuoteView;
GO

TRUNCATE TABLE perf.ReadLog;
GO

/* ============================================================================
   1.  The five queries, and the harness that measures them.
   ----------------------------------------------------------------------------
   The queries live in procedures for three reasons: each is written once
   instead of four times, each stage recompiles cleanly, and SET STATISTICS
   PROFILE in 11_actual_plans.sql then reports a short statement rather than
   echoing a wall of text into the plan output.

   None of them takes a parameter. That is deliberate: with a parameter, the
   plan cached for CountryCode = 'MT' would be reused for CountryCode = 'IN',
   the tipping point in section 5 would be hidden behind parameter sniffing,
   and this piece would turn into the one about parameter sniffing.

   What a production version of this table would have that perf.QuoteView does
   not: PRIMARY KEY NONCLUSTERED (ViewId), a foreign key on QuoteId, and a
   CHECK on DwellSeconds. All three are left off so that "one clustered index
   and two non-clustered indexes" is literally what the table has, and so that
   12_write_cost.sql measures index maintenance and not constraint checking.
   ============================================================================ */

/* --- Q1: the dominant read of an event log -- one day's traffic. --------------
   ViewedAt is the leading column of the clustered index, so this is the query
   the clustering choice exists to serve.

   The range is half-open (>= and <), not BETWEEN. ViewedAt is datetime2(3), so
   BETWEEN '2026-02-01' AND '2026-02-02' would include midnight of the second
   day and silently double-count it against the neighbouring day's report. */
CREATE OR ALTER PROCEDURE perf.Q1_ViewsForOneDay
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        ReportDay = CONVERT(date, '2026-02-01'),
        ViewCount = COUNT(*),
        AvgDwell  = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0)),
        MaxDwell  = MAX(v.DwellSeconds)
    FROM perf.QuoteView AS v
    WHERE v.ViewedAt >= '2026-02-01T00:00:00'
      AND v.ViewedAt <  '2026-02-02T00:00:00'
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

/* --- Q2: the ten most recent views of one quote. -----------------------------
   The shape a "recent activity" panel issues. Wants a seek to one QuoteId and
   then the newest rows first, which is why the non-clustered index below keys
   ViewedAt DESC rather than including it. */
CREATE OR ALTER PROCEDURE perf.Q2_RecentViewsOfOneQuote
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (10)
        v.QuoteId,
        v.ViewedAt,
        v.DwellSeconds
    FROM perf.QuoteView AS v
    WHERE v.QuoteId = 42
    ORDER BY v.ViewedAt DESC, v.ViewId DESC
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

/* --- Q3: dwell rollup for three quotes. --------------------------------------
   Touches ~3,750 rows and returns three. This is the query that pays for the
   INCLUDE (DwellSeconds) on the non-clustered index: without it the index can
   find the rows but not answer the question, and 3,750 key lookups is a worse
   plan than reading the table. */
CREATE OR ALTER PROCEDURE perf.Q3_DwellRollupForThreeQuotes
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        v.QuoteId,
        ViewCount = COUNT(*),
        AvgDwell  = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
    FROM perf.QuoteView AS v
    WHERE v.QuoteId IN (7, 42, 63)
    GROUP BY v.QuoteId
    ORDER BY v.QuoteId
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

/* --- Q4: detail rows for a rare country. -------------------------------------
   A few dozen rows out of 100,000. QuoteId and DwellSeconds are not in
   IX_QuoteView_CountryCode, so this is a seek followed by one key lookup per
   matched row -- the case where a narrow, non-covering index is exactly right,
   because a few dozen lookups is nothing.

   No TOP. Capping it would let the heap scan stop early once it had found
   enough rows, which would flatter the "before" number by measuring less work
   than the indexed version does. */
CREATE OR ALTER PROCEDURE perf.Q4_ViewsFromRareCountry
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        v.ViewId,
        v.ViewedAt,
        v.QuoteId,
        v.CountryCode,
        v.DwellSeconds
    FROM perf.QuoteView AS v
    WHERE v.CountryCode = 'MT'
    ORDER BY v.ViewedAt, v.ViewId
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

/* --- Q5: the same shape, on the commonest country. ---------------------------
   Identical predicate column, identical index, and the right answer is the
   opposite one. Over half the table matches, so a seek plus one key lookup per
   row would cost tens of times a straight scan, and the optimiser declines to
   use the index at all. Section 7 forces it to and prices the mistake. */
CREATE OR ALTER PROCEDURE perf.Q5_DwellRollupForCommonCountry
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        CountryCode = 'IN',
        ViewCount   = COUNT(*),
        AvgDwell    = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
    FROM perf.QuoteView AS v
    WHERE v.CountryCode = 'IN'
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

/* --- The harness. ------------------------------------------------------------
   sys.dm_exec_requests.logical_reads is the running total for the request that
   is executing this batch, so the difference across one EXEC is that query's
   logical reads. Reading a DMV costs no pages of its own, and the row written
   to perf.ReadLog is written after the second reading, so neither is inside the
   measurement.

   SET STATISTICS IO is on throughout, so the per-table breakdown appears in
   the captured output above every measurement and is the authoritative record.
   This table is the convenience: it puts twenty numbers in one grid at the end
   instead of asking a reader to comb several hundred lines of messages.

   Two artefacts to expect, both of which make this table read a little high.

   First, each measurement is followed by a `Table 'ReadLog'` line with a
   handful of reads: this procedure storing its own row, not part of the query
   above it.

   Second and larger: the request counter is per request, not per table, so it
   also counts the catalog pages read to *compile* the query. Every measured
   query carries OPTION (RECOMPILE), so each execution recompiles and reads
   sysschobjs, sysidxstats, syscolpars and friends -- roughly 25 to 45 pages,
   visible as their own STATISTICS IO lines. That is noise against a 1,900-page
   scan and most of the number against a three-page covering seek, which is
   exactly why the per-table STATISTICS IO output is the authoritative record
   and section 6a is the convenience.
   --------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE perf.MeasureReads
    @Stage     varchar(30),
    @QueryName varchar(30),
    @ProcName  sysname
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @before bigint =
        (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);

    EXEC @ProcName;

    DECLARE @after bigint =
        (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);

    INSERT perf.ReadLog (Stage, QueryName, LogicalReads)
    VALUES (@Stage, @QueryName, @after - @before);
END
GO

/* --- What is on the table right now. ---------------------------------------- */
CREATE OR ALTER PROCEDURE perf.ShowIndexes
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        IndexName    = ISNULL(i.name, '(heap)'),
        IndexType    = i.type_desc,
        IsUnique     = i.is_unique,
        KeyColumns   = ISNULL(k.Cols, '-'),
        IncludedCols = ISNULL(inc.Cols, '-'),
        UsedPages    = ps.used_page_count,
        UsedMB       = CONVERT(decimal(8,2), ps.used_page_count * 8.0 / 1024)
    FROM sys.indexes AS i
    INNER JOIN sys.dm_db_partition_stats AS ps
            ON ps.object_id = i.object_id
           AND ps.index_id  = i.index_id
    OUTER APPLY (
        SELECT Cols = STRING_AGG(
                          CONVERT(nvarchar(max),
                              c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END),
                          ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
        FROM sys.index_columns AS ic
        INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
               AND c.column_id = ic.column_id
        WHERE ic.object_id           = i.object_id
          AND ic.index_id            = i.index_id
          AND ic.is_included_column  = 0
    ) AS k
    OUTER APPLY (
        SELECT Cols = STRING_AGG(CONVERT(nvarchar(max), c.name), ', ')
                          WITHIN GROUP (ORDER BY c.name)
        FROM sys.index_columns AS ic
        INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
               AND c.column_id = ic.column_id
        WHERE ic.object_id           = i.object_id
          AND ic.index_id            = i.index_id
          AND ic.is_included_column  = 1
    ) AS inc
    WHERE i.object_id = OBJECT_ID('perf.QuoteView')
    ORDER BY i.index_id;
END
GO

/* ============================================================================
   2.  Stage 0 — the heap. Every query's "before".
   ----------------------------------------------------------------------------
   A heap has no order and no navigation structure, so there is exactly one way
   to satisfy any predicate: read every page. Expect all five numbers to be
   roughly the heap's page count from 09 section 4a, regardless of whether the
   query returns 1 row or 55,000. That equality is the whole argument for
   indexes -- the cost of a heap query is a property of the table, not of the
   question.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### STAGE 0 — HEAP. No indexes.                          ###';
PRINT '############################################################';

EXEC perf.ShowIndexes;
GO

PRINT '--- stage 0 / Q1: one day of views (range on ViewedAt) ---';
EXEC perf.MeasureReads '0-heap', 'Q1 one day',        'perf.Q1_ViewsForOneDay';
GO
PRINT '--- stage 0 / Q2: ten most recent views of quote 42 ---';
EXEC perf.MeasureReads '0-heap', 'Q2 recent for one', 'perf.Q2_RecentViewsOfOneQuote';
GO
PRINT '--- stage 0 / Q3: dwell rollup for three quotes ---';
EXEC perf.MeasureReads '0-heap', 'Q3 rollup 3 quotes','perf.Q3_DwellRollupForThreeQuotes';
GO
PRINT '--- stage 0 / Q4: detail rows for rare country MT ---';
EXEC perf.MeasureReads '0-heap', 'Q4 rare country',   'perf.Q4_ViewsFromRareCountry';
GO
PRINT '--- stage 0 / Q5: dwell rollup for common country IN ---';
EXEC perf.MeasureReads '0-heap', 'Q5 common country', 'perf.Q5_DwellRollupForCommonCountry';
GO

/* ============================================================================
   3.  Stage 1 — the clustered index.
   ----------------------------------------------------------------------------
   CREATE UNIQUE CLUSTERED INDEX CIX_QuoteView_ViewedAt
       ON perf.QuoteView (ViewedAt, ViewId);

   A clustered index is not a structure beside the table, it *is* the table:
   the leaf level holds the rows themselves, in key order. That is why there
   can only be one, and why choosing it is a decision about physical storage
   rather than about one query.

   Why (ViewedAt, ViewId):

     * Ever-increasing. Every insert lands at the right-hand end of the index,
       so pages fill and are left alone. A key that inserts into the middle
       splits pages instead, and 12_write_cost.sql measures what that costs.
     * It is the dominant predicate. Practically every question asked of an
       event log is bounded by time, and only the clustered index can serve a
       range without a lookup per row, because the row is already there.
     * Narrow. 15 bytes. The clustering key is copied into every row of every
       non-clustered index on the table, so its width is a tax paid four times
       over, not once.

   Why UNIQUE, which is the part that is easy to leave off: ViewedAt alone has
   duplicates in general, and a non-unique clustered index makes SQL Server
   append a hidden 4-byte uniquifier to duplicate keys -- carried, like the rest
   of the key, in every non-clustered index row. Adding ViewId, which is unique
   by construction, buys the same guarantee with bytes that are useful.

   And it is created before the non-clustered indexes on purpose: see section 0.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### STAGE 1 — + UNIQUE CLUSTERED (ViewedAt, ViewId)      ###';
PRINT '############################################################';

CREATE UNIQUE CLUSTERED INDEX CIX_QuoteView_ViewedAt
    ON perf.QuoteView (ViewedAt, ViewId);
GO

EXEC perf.ShowIndexes;
GO

PRINT '--- stage 1 / Q1: one day of views (should now be a range seek) ---';
EXEC perf.MeasureReads '1-clustered', 'Q1 one day',        'perf.Q1_ViewsForOneDay';
GO
PRINT '--- stage 1 / Q2 ---';
EXEC perf.MeasureReads '1-clustered', 'Q2 recent for one', 'perf.Q2_RecentViewsOfOneQuote';
GO
PRINT '--- stage 1 / Q3 ---';
EXEC perf.MeasureReads '1-clustered', 'Q3 rollup 3 quotes','perf.Q3_DwellRollupForThreeQuotes';
GO
PRINT '--- stage 1 / Q4 ---';
EXEC perf.MeasureReads '1-clustered', 'Q4 rare country',   'perf.Q4_ViewsFromRareCountry';
GO
PRINT '--- stage 1 / Q5 ---';
EXEC perf.MeasureReads '1-clustered', 'Q5 common country', 'perf.Q5_DwellRollupForCommonCountry';
GO

/* ============================================================================
   4.  Stage 2 — the first non-clustered index. Covering, on purpose.
   ----------------------------------------------------------------------------
   CREATE NONCLUSTERED INDEX IX_QuoteView_QuoteId_ViewedAt
       ON perf.QuoteView (QuoteId, ViewedAt DESC, ViewId DESC)
       INCLUDE (DwellSeconds);

   A non-clustered index is a separate, narrower copy of some columns, kept in
   its own order, with a pointer back to the row. Reading it is cheap; following
   the pointer is not, and everything interesting about non-clustered indexes
   follows from that asymmetry.

   Key order reads straight off Q2's requirement -- equality on QuoteId, then
   newest first: `WHERE QuoteId = 42 ORDER BY ViewedAt DESC, ViewId DESC`. Both
   sort columns are in the key, in that direction, so the ORDER BY is satisfied
   by the index order and the plan has no Sort in it. ViewId DESC is in the key
   rather than left implicit for the same reason 05/06 gave in Day 7: a
   tiebreaker outside the key order still forces a sort.

   INCLUDE (DwellSeconds) is what makes this index worth having for Q3. Included
   columns live only in the leaf, so they widen the index by 4 bytes a row and
   do nothing to its depth or its ordering -- and in exchange Q3 never has to
   leave the index. Without it, Q3's plan is a seek plus ~3,750 key lookups,
   and the optimiser would sensibly refuse it and scan instead. The column is
   in INCLUDE and not in the key because Q3 aggregates DwellSeconds, it never
   searches or sorts by it.

   ViewedAt and ViewId are also the clustering key, so they cost nothing extra
   in the leaf: they would be carried as the row locator whether or not they
   were named.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### STAGE 2 — + NC (QuoteId, ViewedAt DESC, ViewId DESC) ###';
PRINT '###            INCLUDE (DwellSeconds)                    ###';
PRINT '############################################################';

CREATE NONCLUSTERED INDEX IX_QuoteView_QuoteId_ViewedAt
    ON perf.QuoteView (QuoteId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DwellSeconds);
GO

EXEC perf.ShowIndexes;
GO

PRINT '--- stage 2 / Q1 ---';
EXEC perf.MeasureReads '2-plus-nc1', 'Q1 one day',        'perf.Q1_ViewsForOneDay';
GO
PRINT '--- stage 2 / Q2: should now be a covering seek with no Sort ---';
EXEC perf.MeasureReads '2-plus-nc1', 'Q2 recent for one', 'perf.Q2_RecentViewsOfOneQuote';
GO
PRINT '--- stage 2 / Q3: should now be covered -- no key lookups ---';
EXEC perf.MeasureReads '2-plus-nc1', 'Q3 rollup 3 quotes','perf.Q3_DwellRollupForThreeQuotes';
GO
PRINT '--- stage 2 / Q4 ---';
EXEC perf.MeasureReads '2-plus-nc1', 'Q4 rare country',   'perf.Q4_ViewsFromRareCountry';
GO
PRINT '--- stage 2 / Q5 ---';
EXEC perf.MeasureReads '2-plus-nc1', 'Q5 common country', 'perf.Q5_DwellRollupForCommonCountry';
GO

/* ============================================================================
   5.  Stage 3 — the second non-clustered index. Narrow, and not covering.
   ----------------------------------------------------------------------------
   CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode
       ON perf.QuoteView (CountryCode);

   Two bytes of key plus the 15-byte clustering key. About a tenth the size of
   the table, which is the entire reason it is useful and also the reason it is
   dangerous: it can find any row by country and it can produce almost nothing
   about that row without going back to the table.

   Q4 and Q5 are the same query shape over the same column through the same
   index, and the right plan for each is the opposite of the right plan for the
   other. Q4 matches a few dozen rows, so a seek and a few dozen key lookups
   win easily. Q5 matches over half the table, so the same plan would perform
   tens of thousands of lookups, and the optimiser is expected to ignore the
   index and scan the clustered index instead.

   Watch Q5's number below: it should not improve. An index existing, being
   perfectly applicable to the predicate, and being correctly declined is the
   single most useful thing on this page, because it is the failure mode that
   survives a code review -- the DDL looks right, the query looks right, and
   the index is dead weight that only slows the writes down.
   ============================================================================ */
PRINT '';
PRINT '############################################################';
PRINT '### STAGE 3 — + NC (CountryCode), narrow, non-covering   ###';
PRINT '############################################################';

CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode
    ON perf.QuoteView (CountryCode);
GO

EXEC perf.ShowIndexes;
GO

PRINT '--- stage 3 / Q1 ---';
EXEC perf.MeasureReads '3-plus-nc2', 'Q1 one day',        'perf.Q1_ViewsForOneDay';
GO
PRINT '--- stage 3 / Q2 ---';
EXEC perf.MeasureReads '3-plus-nc2', 'Q2 recent for one', 'perf.Q2_RecentViewsOfOneQuote';
GO
PRINT '--- stage 3 / Q3 ---';
EXEC perf.MeasureReads '3-plus-nc2', 'Q3 rollup 3 quotes','perf.Q3_DwellRollupForThreeQuotes';
GO
PRINT '--- stage 3 / Q4: rare country -- seek plus a few dozen key lookups ---';
EXEC perf.MeasureReads '3-plus-nc2', 'Q4 rare country',   'perf.Q4_ViewsFromRareCountry';
GO
PRINT '--- stage 3 / Q5: common country -- expect NO improvement ---';
EXEC perf.MeasureReads '3-plus-nc2', 'Q5 common country', 'perf.Q5_DwellRollupForCommonCountry';
GO

/* ============================================================================
   6.  The answer: logical reads before and after each index.
   ============================================================================ */
PRINT '';
PRINT '=== 6a.  Logical reads by query and stage ===';

/* PlusClustered rather than the obvious Clustered: CLUSTERED is a reserved
   word in T-SQL and will not serve as a bare column alias. The name also lines
   up with PlusNC1 and PlusNC2, so the four columns read as one staircase. */
SELECT
    QueryName,
    Heap          = MAX(CASE WHEN Stage = '0-heap'      THEN LogicalReads END),
    PlusClustered = MAX(CASE WHEN Stage = '1-clustered' THEN LogicalReads END),
    PlusNC1       = MAX(CASE WHEN Stage = '2-plus-nc1'  THEN LogicalReads END),
    PlusNC2       = MAX(CASE WHEN Stage = '3-plus-nc2'  THEN LogicalReads END)
FROM perf.ReadLog
WHERE Stage IN ('0-heap', '1-clustered', '2-plus-nc1', '3-plus-nc2')
GROUP BY QueryName
ORDER BY QueryName;
GO

PRINT '=== 6b.  The same thing as a ratio against the heap baseline ===';

/* Ratios rather than differences, because the interesting claim is "an order
   of magnitude", not "1,900 pages". A value above 1.00 means the index made
   that query read more, which happens and is worth seeing. */
SELECT
    r.QueryName,
    HeapReads  = h.LogicalReads,
    FinalReads = r.LogicalReads,
    TimesCheaper = CONVERT(decimal(10,2),
                       CASE WHEN r.LogicalReads = 0 THEN NULL
                            ELSE h.LogicalReads * 1.0 / r.LogicalReads END)
FROM perf.ReadLog AS r
INNER JOIN perf.ReadLog AS h
        ON h.QueryName = r.QueryName
       AND h.Stage     = '0-heap'
WHERE r.Stage = '3-plus-nc2'
ORDER BY TimesCheaper DESC, r.QueryName;
GO

PRINT '=== 6c.  What the three indexes cost in space ===';

EXEC perf.ShowIndexes;
GO

/* ============================================================================
   7.  Three things the staircase does not show.
   ----------------------------------------------------------------------------
   Everything below is beyond what the exercise asked for, and the extra index
   created in 7b is dropped again at the end so the table's final shape is
   exactly the three indexes in the DDL above.
   ============================================================================ */

/* --- 7a.  Pricing the plan the optimiser refused. ---------------------------
   Q5's numbers say the CountryCode index did not help. This says why, by
   forcing the seek the optimiser declined: INDEX() makes it use that index and
   FORCESEEK forbids scanning it, which together leave only seek-plus-lookup.

   Expect the reads to land tens of times above the heap baseline. This is the
   shape of an "index tuning" change that adds an index, passes review, and
   makes a report slower -- and would not have been caught by checking that the
   index is used, because it is. */
CREATE OR ALTER PROCEDURE perf.X1_CommonCountryForcedSeek
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        CountryCode = 'IN',
        ViewCount   = COUNT(*),
        AvgDwell    = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
    FROM perf.QuoteView AS v WITH (INDEX (IX_QuoteView_CountryCode), FORCESEEK)
    WHERE v.CountryCode = 'IN'
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

PRINT '';
PRINT '=== 7a.  Forcing the seek the optimiser refused, and pricing it ===';
EXEC perf.MeasureReads '4-extra', 'Q5 forced seek', 'perf.X1_CommonCountryForcedSeek';
GO

/* --- 7b.  The fix is not a better predicate, it is INCLUDE. -----------------
   The lookups are the cost, so remove the lookups. With DwellSeconds in the
   leaf the index answers Q5 without touching the table, and a seek over half a
   narrow index beats a scan of the whole wide one.

   This is a third non-clustered index and therefore outside the exercise, so
   it is created, measured and dropped. In production it would replace
   IX_QuoteView_CountryCode rather than join it: a covering index whose key is
   a prefix of another index's key makes that other index redundant, and
   keeping both means paying twice on every write for one answer. */
PRINT '';
PRINT '=== 7b.  The covering version of the same index ===';

CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode_Covering
    ON perf.QuoteView (CountryCode)
    INCLUDE (DwellSeconds);
GO

EXEC perf.MeasureReads '4-extra', 'Q5 with covering NC', 'perf.Q5_DwellRollupForCommonCountry';
GO

DROP INDEX IX_QuoteView_CountryCode_Covering ON perf.QuoteView;
GO

/* --- 7c.  A non-clustered index earning its keep on a query that never
           mentions its column. -----------------------------------------------
   COUNT(*) names no column at all, so any structure covering every row will
   do -- and the optimiser picks the smallest one, which is the CountryCode
   index at roughly a tenth of the table. Nothing about the query hints at
   CountryCode. This is the counterweight to 7a: the same index that is a trap
   for Q5 is free money here. */
CREATE OR ALTER PROCEDURE perf.X2_CountAllViews
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TotalViews = COUNT(*)
    FROM perf.QuoteView
    OPTION (MAXDOP 1, RECOMPILE);
END
GO

PRINT '';
PRINT '=== 7c.  COUNT(*) served by the narrowest index on the table ===';
EXEC perf.MeasureReads '4-extra', 'COUNT(*) all rows', 'perf.X2_CountAllViews';
GO

PRINT '=== 7d.  The extras, side by side with the heap and the final state ===';

SELECT
    QueryName,
    LogicalReads,
    VsHeapBaseline = CONVERT(decimal(10,2),
                         LogicalReads * 1.0 /
                         NULLIF((SELECT MAX(LogicalReads) FROM perf.ReadLog
                                 WHERE Stage = '0-heap'), 0))
FROM perf.ReadLog
WHERE Stage = '4-extra'
ORDER BY Seq;
GO

PRINT '=== 7e.  Final index state -- back to one clustered plus two non-clustered ===';

EXEC perf.ShowIndexes;
GO

SET STATISTICS IO OFF;
GO

PRINT 'Index lab complete. Run 11_actual_plans.sql for the plans behind these numbers.';
GO
