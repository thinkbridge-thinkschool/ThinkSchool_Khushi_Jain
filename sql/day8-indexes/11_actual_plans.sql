/* ============================================================================
   Day 8 — the actual execution plans behind the numbers
   ----------------------------------------------------------------------------
   10_index_lab.sql reports what each query cost. This reports why, in the
   optimiser's own words.

   Actual, not estimated. SET SHOWPLAN_TEXT -- which Day 7's 06_plans.sql used
   -- compiles a query and returns the plan without running it, so every row
   count in it is a guess. SET STATISTICS PROFILE executes the query and returns
   the plan it actually used, with an Rows column holding the rows that really
   flowed through each operator and an Executes column holding how many times
   each operator ran. The gap between Rows and EstimateRows is where bad plans
   come from, and only the actual plan has both.

   How to read the output
   ----------------------
   Each statement produces one row per plan operator, deepest first, indented by
   the |--  markers in StmtText. Five columns carry almost all the meaning:

       StmtText     the operator and its arguments, indented by tree depth
       Rows         rows that actually left this operator
       Executes     how many times it ran -- above 1 means it is on the inner
                    side of a loop, and Rows is the total across all of them
       EstimateRows what the optimiser expected, for comparison with Rows
       Argument     the index actually touched, and the seek predicate

   The rest of the columns are cost estimates, useful for comparing two plans
   for the same query and not much else.

   For the same plans graphically, run any of these procedures in SSMS or Azure
   Data Studio with Ctrl+M (Include Actual Execution Plan), or SET STATISTICS
   XML ON and save the single XML cell as a .sqlplan file.

   Prerequisite: 10_index_lab.sql, which leaves the three indexes in place. Run
   against a heap this script produces five table scans and says nothing.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   0.  Refuse to run against the wrong index state.
   ----------------------------------------------------------------------------
   Silently producing five heap scans and calling them "the plans" is worse than
   failing, because the output looks like a result.
   ============================================================================ */
IF  OBJECT_ID('perf.QuoteView') IS NULL
 OR NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('perf.QuoteView')
                  AND name = 'CIX_QuoteView_ViewedAt')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('perf.QuoteView')
                  AND name = 'IX_QuoteView_QuoteId_ViewedAt')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('perf.QuoteView')
                  AND name = 'IX_QuoteView_CountryCode')
BEGIN
    THROW 50801, 'perf.QuoteView is missing one or more Day-8 indexes. Run 09_build_dataset.sql then 10_index_lab.sql first.', 1;
END
GO

PRINT '=== 0.  Indexes these plans were captured against ===';

EXEC perf.ShowIndexes;
GO

/* ============================================================================
   1.  Snapshot the per-index operational counters.
   ----------------------------------------------------------------------------
   sys.dm_db_index_operational_stats counts, per index, how many range scans and
   how many singleton lookups have been served since the database came online.
   Taking a snapshot here and differencing it in section 3 turns the plans below
   into arithmetic: singleton_lookup_count on the clustered index is exactly the
   number of key lookups the non-clustered plans forced, which is the quantity
   the whole clustered-versus-non-clustered argument turns on.

   Operational stats are maintained in memory as the work happens, unlike
   sys.dm_db_index_usage_stats which is flushed periodically -- so the counters
   are already correct by the time section 3 reads them.
   ============================================================================ */
/* The DROP is fenced into its own batch so that SELECT ... INTO never compiles
   against a temp table that is still in scope from an earlier run of this script
   on the same connection. Temp tables outlive a GO, so #OpStatsBefore is still
   there for section 3. */
DROP TABLE IF EXISTS #OpStatsBefore;
GO

SELECT
    os.index_id,
    os.range_scan_count,
    os.singleton_lookup_count
INTO #OpStatsBefore
FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL) AS os;
GO

/* ============================================================================
   2.  The five actual plans.
   ============================================================================ */
SET STATISTICS PROFILE ON;
GO

PRINT '=== Q1 -- look for: Clustered Index Seek on CIX_QuoteView_ViewedAt, ===';
PRINT '===        one Executes, Rows about 1,100, no Sort, no lookup.      ===';
/* The seek predicate in Argument should show the half-open range on ViewedAt.
   The rows are the leaf level of the clustered index, so the aggregate has
   DwellSeconds to hand without a second structure being touched -- which is
   what "the clustered index is the table" means operationally. */
EXEC perf.Q1_ViewsForOneDay;
GO

PRINT '=== Q2 -- look for: Top over an Index Seek on                       ===';
PRINT '===        IX_QuoteView_QuoteId_ViewedAt, ORDERED FORWARD, no Sort. ===';
/* The absence of a Sort is the thing to check. The index is keyed (QuoteId,
   ViewedAt DESC, ViewId DESC) and the query orders by ViewedAt DESC, ViewId
   DESC, so the rows arrive in the order the ORDER BY asks for and Top can stop
   after ten. Rows on the seek should be 10, not 1,250: a Top over an ordered
   seek reads only what it consumes. Reverse either direction in the DDL and a
   Sort appears over all 1,250 rows. */
EXEC perf.Q2_RecentViewsOfOneQuote;
GO

PRINT '=== Q3 -- look for: Index Seek on IX_QuoteView_QuoteId_ViewedAt     ===';
PRINT '===        with three Executes and NO Key Lookup anywhere.          ===';
/* Three seeks, one per value in the IN list, roughly 3,750 rows in total, and
   the aggregate computed straight off the index. DwellSeconds is in the leaf
   because of INCLUDE, so the plan never reaches the table. Drop the INCLUDE
   and this plan changes shape completely -- either 3,750 Key Lookups or, more
   likely, the optimiser abandoning the index for a clustered index scan. */
EXEC perf.Q3_DwellRollupForThreeQuotes;
GO

PRINT '=== Q4 -- look for: Index Seek on IX_QuoteView_CountryCode feeding  ===';
PRINT '===        a Nested Loops with a Key Lookup, Executes = matched rows.===';
/* This is the canonical non-covering plan and it is the right plan here. The
   seek finds a few dozen rows, and Executes on the Key Lookup equals that
   count -- one round trip to the clustered index per matched row, each costing
   as many reads as the clustered index has levels. A few dozen of those is
   cheaper than reading 100,000 rows, so the index wins. */
EXEC perf.Q4_ViewsFromRareCountry;
GO

PRINT '=== Q5 -- look for: Clustered Index Scan. The CountryCode index is  ===';
PRINT '===        applicable, available, and correctly not used.           ===';
/* Same column, same index, same query shape as Q4, and the plan is a full scan
   of the clustered index with a residual predicate on CountryCode. Over half
   the table matches, so Q4's plan would run tens of thousands of key lookups.
   Section 7a of 10_index_lab.sql forces exactly that and prices it.

   The number to check is EstimateRows against Rows on the scan: the FULLSCAN
   histogram on CountryCode is why the estimate is close, and a close estimate
   is why this decision is right. On sampled statistics over skewed data the
   same query can be planned either way. */
EXEC perf.Q5_DwellRollupForCommonCountry;
GO

SET STATISTICS PROFILE OFF;
GO

/* ============================================================================
   3.  The plans as counters: seeks, scans, and key lookups.
   ----------------------------------------------------------------------------
   RangeScans is seeks and scans of that index; SingletonLookups on the
   clustered index is the count of key lookups the non-clustered plans forced.

   Expect a small non-zero SingletonLookups on CIX_QuoteView_ViewedAt, matching
   the rows Q4 returned, and zero attributable to Q1, Q2 and Q3 -- one of them
   reads the clustered index directly and the other two are covered.
   ============================================================================ */
PRINT '=== 3.  Per-index access counts for the five statements above ===';

SELECT
    IndexName        = ISNULL(i.name, '(heap)'),
    IndexType        = i.type_desc,
    RangeScans       = a.range_scan_count       - ISNULL(b.range_scan_count, 0),
    SingletonLookups = a.singleton_lookup_count - ISNULL(b.singleton_lookup_count, 0)
FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL) AS a
INNER JOIN sys.indexes AS i
        ON i.object_id = a.object_id
       AND i.index_id  = a.index_id
LEFT JOIN #OpStatsBefore AS b
       ON b.index_id = a.index_id
ORDER BY a.index_id;
GO

DROP TABLE IF EXISTS #OpStatsBefore;
GO

PRINT 'Plans captured. Run 12_write_cost.sql for the other half of the trade.';
GO
