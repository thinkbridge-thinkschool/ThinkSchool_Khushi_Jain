/* ============================================================================
   Day 8 — the dataset the index measurements run against
   ----------------------------------------------------------------------------
   Builds perf.QuoteView: 100,000 rows of synthetic view-log events, created as
   a HEAP with no index of any kind. The heap is the point. Every "before"
   number in 10_index_lab.sql is a measurement of this table with nothing to
   help it, and you cannot measure the arrival of a clustered index on a table
   that already has one.

   Why a new table rather than app.Quote
   ------------------------------------
     * app.Quote holds 80 rows. Eighty rows of anything fit on one or two
       pages, so a seek and a scan cost the same and every index decision is
       unmeasurable. The exercise asks for ~100k rows for exactly this reason.
     * The three Day-7 pieces are already submitted against the seed, and the
       captured output under each day7 folder's results directory is the
       evidence for them. Adding 100,000 rows to app.Quote would silently
       invalidate all three.
     * An append-only event log is the shape where the clustered/non-clustered
       distinction actually bites: one dominant time-range predicate, several
       secondary lookup predicates, and a write rate high enough that index
       maintenance is a cost somebody pays.

   perf.QuoteView carries no foreign keys, no primary key and no constraints,
   which is deliberate rather than lazy. A foreign key is enforced by a read
   against another index on every insert, and 12_write_cost.sql is trying to
   measure the cost of index maintenance specifically -- so anything else that
   does per-row work would be counted as index maintenance and would not be.
   The commentary in 10_index_lab.sql section 1 says what a production version
   of this table would declare instead.

   Reproducibility
   ---------------
   Every column is derived arithmetically from the row number through SHA2_256.
   No RAND(), no NEWID(), no GETDATE(). The row *set* is therefore identical on
   every run and every machine, so the page counts and logical-read numbers the
   next script reports are comparable across runs rather than being one
   afternoon's weather.

   Runtime: a few seconds. Target: SQL Server 2022, Azure SQL compatible.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================================================================
   0.  Server context.
   ----------------------------------------------------------------------------
   Printed because three of the numbers later in this lab depend on it. The
   recovery model decides how much transaction log an insert generates, which
   12_write_cost.sql measures directly; the edition decides whether an index
   build can go parallel; the compatibility level decides which cardinality
   estimator produced the plans in 11_actual_plans.sql.
   ============================================================================ */
PRINT '=== 0.  Server and database context ===';

SELECT
    ProductVersion = CONVERT(varchar(20), SERVERPROPERTY('ProductVersion')),
    Edition        = CONVERT(varchar(40), SERVERPROPERTY('Edition')),
    CompatLevel    = d.compatibility_level,
    RecoveryModel  = d.recovery_model_desc
FROM sys.databases AS d
WHERE d.database_id = DB_ID();
GO

/* ============================================================================
   1.  The schema and the tables.
   ----------------------------------------------------------------------------
   `perf` rather than `app`: everything in here exists to be measured, and
   keeping it out of the domain schema means a reader can tell at a glance that
   no application object depends on it. Dropping the whole schema costs nothing.

   CREATE SCHEMA has to be the only statement in its batch, hence the EXEC.
   ============================================================================ */
IF SCHEMA_ID('perf') IS NULL
    EXEC ('CREATE SCHEMA perf AUTHORIZATION dbo;');
GO

/* Everything in the schema, including the four write-cost clones 12 builds, so
   that re-running this script leaves no table behind from an earlier run. */
DROP TABLE IF EXISTS perf.QuoteView;
DROP TABLE IF EXISTS perf.ReadLog;
DROP TABLE IF EXISTS perf.WriteLog;
DROP TABLE IF EXISTS perf.WriteSource;
DROP TABLE IF EXISTS perf.WriteHeap, perf.WriteClustered, perf.WriteIndexed, perf.WriteRandomKey;
GO

/* ---------------------------------------------------------------------------
   perf.QuoteView — one row per time somebody looked at a quote.

   The two varchar columns are not padding. A view log really does carry a user
   agent and a referrer, and they are what make the row wide enough for the
   exercise to be honest: at roughly 160 bytes a row the table is ~2,000 pages,
   so a covering index on three narrow columns is genuinely an order of
   magnitude smaller than the table. On a table of four int columns it would
   not be, and every conclusion about covering indexes would be an artefact of
   the fixture.

   Created as a heap. No index, no key, no constraint.
   --------------------------------------------------------------------------- */
CREATE TABLE perf.QuoteView
(
    ViewId       bigint       NOT NULL,
    QuoteId      int          NOT NULL,
    UserId       int          NOT NULL,
    ViewedAt     datetime2(3) NOT NULL,
    CountryCode  char(2)      NOT NULL,
    DeviceType   varchar(10)  NOT NULL,
    DwellSeconds int          NOT NULL,
    UserAgent    varchar(120) NOT NULL,
    Referrer     varchar(120) NULL
);
GO

/* ---------------------------------------------------------------------------
   The two measurement logs. perf.MeasureReads in the next script writes to
   ReadLog; 12_write_cost.sql writes to WriteLog. Both exist so the before and
   after numbers end up in one printable table instead of scattered across a
   few hundred lines of SET STATISTICS IO messages that a reader has to
   reassemble by hand.
   --------------------------------------------------------------------------- */
CREATE TABLE perf.ReadLog
(
    Seq          int IDENTITY(1,1) NOT NULL,
    Stage        varchar(30)       NOT NULL,
    QueryName    varchar(30)       NOT NULL,
    LogicalReads bigint            NOT NULL,
    CONSTRAINT PK_ReadLog PRIMARY KEY CLUSTERED (Seq)
);
GO

CREATE TABLE perf.WriteLog
(
    Seq          int IDENTITY(1,1) NOT NULL,
    Scenario     varchar(40)       NOT NULL,
    Structure    varchar(30)       NOT NULL,
    RowsTouched  int               NOT NULL,
    LogicalReads bigint            NOT NULL,
    LogBytes     bigint            NOT NULL,
    CONSTRAINT PK_WriteLog PRIMARY KEY CLUSTERED (Seq)
);
GO

/* ============================================================================
   2.  Generate 100,000 rows.
   ----------------------------------------------------------------------------
   Numbers is five cross joins of the digits 0-9, which is exactly 0..99999 --
   no dependency on sys.all_objects, whose row count differs between server
   versions and would make "100,000 rows" a promise this script could not keep.

   Every column then comes out of one SHA2_256 of the row number, sliced into
   six independent two-byte draws. A two-byte varbinary converts to an int in
   0..65535, which is a uniform pseudo-random draw that is also completely
   deterministic -- the property RAND() and NEWID() cannot give.

   ViewedAt is the exception: it is n * 78 seconds after a fixed instant, so it
   increases monotonically with ViewId. That is not decoration. An event log's
   timestamp is ever-increasing in real life, an ever-increasing clustering key
   appends rather than splitting pages, and 12_write_cost.sql measures what
   happens when that property is given up.
   ============================================================================ */
PRINT '=== 2.  Generating 100,000 rows into the heap ===';

WITH Digits(d) AS
(
    SELECT d FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) AS v(d)
),
Numbers AS
(
    SELECT n = d1.d + d2.d * 10 + d3.d * 100 + d4.d * 1000 + d5.d * 10000
    FROM Digits AS d1
    CROSS JOIN Digits AS d2
    CROSS JOIN Digits AS d3
    CROSS JOIN Digits AS d4
    CROSS JOIN Digits AS d5
),
Draws AS
(
    SELECT
        n,
        /* Six two-byte slices of one hash. Each is 0..65535 and they are
           independent of one another, so QuoteId does not correlate with
           CountryCode and the skew in one column cannot leak into another. */
        d1 = CONVERT(int, SUBSTRING(h.RowHash,  1, 2)),
        d2 = CONVERT(int, SUBSTRING(h.RowHash,  3, 2)),
        d3 = CONVERT(int, SUBSTRING(h.RowHash,  5, 2)),
        d4 = CONVERT(int, SUBSTRING(h.RowHash,  7, 2)),
        d5 = CONVERT(int, SUBSTRING(h.RowHash,  9, 2)),
        d6 = CONVERT(int, SUBSTRING(h.RowHash, 11, 2))
    FROM Numbers AS num
    CROSS APPLY (SELECT RowHash = HASHBYTES('SHA2_256', CONVERT(varbinary(8), CONVERT(bigint, num.n)))) AS h
)
INSERT perf.QuoteView
    (ViewId, QuoteId, UserId, ViewedAt, CountryCode, DeviceType, DwellSeconds, UserAgent, Referrer)
SELECT
    ViewId   = CONVERT(bigint, n) + 1,

    /* 80 quotes, ~1,250 views each. Chosen to match the seed's quote count so
       the numbers feel like the same application, but there is no foreign key
       and no row in app.Quote is read: see the header. */
    QuoteId  = 1 + d2 % 80,

    UserId   = 1 + d3 % 2500,

    /* 78 seconds apart, jittered by up to 999ms. The jitter is smaller than
       the step, so the sequence is still strictly increasing -- ViewedAt is a
       legitimate leading clustering key. 100,000 * 78s is a little over 90
       days of history. */
    ViewedAt = DATEADD(MILLISECOND, d1 % 1000,
                       DATEADD(SECOND, n * 78, CONVERT(datetime2(3), '2026-01-01T00:00:00'))),

    /* Deliberately, savagely skewed. 'IN' is the majority of the table and
       'MT' is a few dozen rows out of 100,000. Section 5 of 10_index_lab.sql
       needs both: the same index, the same query shape and the same column
       give a seek for one value and a full scan for the other, and no amount
       of reading the DDL tells you which. Real traffic looks like this; a
       uniform test fixture is what hides the tipping point until production
       finds it. */
    CountryCode = CASE
                      WHEN d4 % 10000 < 5200 THEN 'IN'
                      WHEN d4 % 10000 < 7600 THEN 'US'
                      WHEN d4 % 10000 < 8800 THEN 'GB'
                      WHEN d4 % 10000 < 9500 THEN 'DE'
                      WHEN d4 % 10000 < 9900 THEN 'BR'
                      WHEN d4 % 10000 < 9997 THEN 'JP'
                      ELSE                        'MT'
                  END,

    DeviceType = CASE
                     WHEN d5 % 100 < 58 THEN 'mobile'
                     WHEN d5 % 100 < 92 THEN 'desktop'
                     ELSE                    'tablet'
                 END,

    DwellSeconds = 2 + d6 % 300,

    /* Derived from the same draw as DeviceType, so the agent string agrees
       with the device rather than contradicting it. */
    UserAgent = CASE
                    WHEN d5 % 100 < 58 THEN 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) Safari/605.1'
                    WHEN d5 % 100 < 92 THEN 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0 Safari/537.36'
                    ELSE                    'Mozilla/5.0 (Linux; Android 14; Tablet) Chrome/124.0.0.0 Safari/537.36'
                END,

    /* example.com and example.org are reserved by RFC 2606 for exactly this.
       NULL is the "arrived directly" case, which is a real fifth of traffic
       and not a gap in the generator. */
    Referrer = CASE d6 % 5
                   WHEN 0 THEN 'https://example.com/quotes/browse?page=2'
                   WHEN 1 THEN 'https://example.com/collections/favourites'
                   WHEN 2 THEN 'https://example.com/search?q=stoicism'
                   WHEN 3 THEN 'https://example.org/blog/on-notation'
                   ELSE        NULL
               END
FROM Draws;
GO

/* ============================================================================
   3.  Statistics, created with FULLSCAN before anything is measured.
   ----------------------------------------------------------------------------
   Two reasons, and the second is the one that would otherwise quietly corrupt
   the results.

   First, plan stability. A sampled statistic on a skewed column can be off by
   enough to change which index the optimiser picks, and a sample rate that
   varies with server build would make the plans in 11_actual_plans.sql
   unreproducible. FULLSCAN removes the sampling.

   Second, measurement hygiene. With AUTO_CREATE_STATISTICS on -- the default --
   the *first* query to filter on CountryCode builds that statistic as part of
   its own execution, and the reads it costs land in that query's logical-read
   count. The heap baseline for Q4 and Q5 would be inflated by work that has
   nothing to do with the absence of an index, and the "before" column of the
   whole exercise would be wrong in a direction that flatters the answer.

   Creating these by hand now means every measured query below runs against
   statistics that already exist. When the indexes arrive they bring their own
   equivalent FULLSCAN statistics; these become redundant, and harmlessly so.
   ============================================================================ */
PRINT '=== 3.  Column statistics (FULLSCAN, before any measurement) ===';

CREATE STATISTICS ST_QuoteView_ViewedAt    ON perf.QuoteView (ViewedAt)    WITH FULLSCAN;
CREATE STATISTICS ST_QuoteView_QuoteId     ON perf.QuoteView (QuoteId)     WITH FULLSCAN;
CREATE STATISTICS ST_QuoteView_CountryCode ON perf.QuoteView (CountryCode) WITH FULLSCAN;
GO

/* ============================================================================
   4.  What was built.
   ----------------------------------------------------------------------------
   UsedPages is the number that matters most in this lab. A heap has no way to
   answer any question except by reading all of it, so this figure is the
   logical-read count of every "before" measurement in the next script, give or
   take the allocation-map pages. Reading it here first means the baseline is a
   prediction rather than a surprise.
   ============================================================================ */
PRINT '=== 4a.  Size of the heap ===';

SELECT
    Structure     = 'heap (no index)',
    TotalRows     = ps.row_count,
    UsedPages     = ps.used_page_count,
    ReservedPages = ps.reserved_page_count,
    UsedMB        = CONVERT(decimal(8,2), ps.used_page_count * 8.0 / 1024)
FROM sys.dm_db_partition_stats AS ps
WHERE ps.object_id = OBJECT_ID('perf.QuoteView')
  AND ps.index_id  = 0;
GO

PRINT '=== 4b.  Row width and page fullness ===';

/* avg_record_size_in_bytes is the honest row width including overhead, and it
   is what turns "2,000 pages" from a magic number into arithmetic: 8,096
   usable bytes per page divided by this figure is the rows per page. */
SELECT
    IndexId            = ips.index_id,
    ips.index_type_desc,
    ips.page_count,
    AvgRecordBytes     = CONVERT(decimal(8,2), ips.avg_record_size_in_bytes),
    AvgPageFullnessPct = CONVERT(decimal(5,2), ips.avg_page_space_used_in_percent)
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL, 'DETAILED') AS ips
WHERE ips.index_level = 0
ORDER BY ips.index_id;
GO

PRINT '=== 4c.  The CountryCode skew the tipping-point section depends on ===';

SELECT
    v.CountryCode,
    Views    = COUNT(*),
    SharePct = CONVERT(decimal(6,3), 100.0 * COUNT(*) / SUM(COUNT(*)) OVER ())
FROM perf.QuoteView AS v
GROUP BY v.CountryCode
ORDER BY Views DESC, v.CountryCode;
GO

PRINT '=== 4d.  Shape of the rest of the data ===';

SELECT
    DistinctQuotes = COUNT(DISTINCT v.QuoteId),
    DistinctUsers  = COUNT(DISTINCT v.UserId),
    FirstView      = MIN(v.ViewedAt),
    LastView       = MAX(v.ViewedAt),
    SpanDays       = DATEDIFF(DAY, MIN(v.ViewedAt), MAX(v.ViewedAt))
FROM perf.QuoteView AS v;
GO

PRINT '=== 4e.  Selectivity of each measured predicate ===';

/* The four numbers that decide whether an index is worth having. An index pays
   when the predicate returns few enough rows that fetching them individually
   beats reading everything; these are the "few enough" figures, stated before
   any index exists to argue about. */
SELECT
    Q1_OneDay_ViewedAt   = (SELECT COUNT(*) FROM perf.QuoteView
                            WHERE ViewedAt >= '2026-02-01T00:00:00'
                              AND ViewedAt <  '2026-02-02T00:00:00'),
    Q2_Q3_OneQuote       = (SELECT COUNT(*) FROM perf.QuoteView WHERE QuoteId = 42),
    Q4_RareCountry_MT    = (SELECT COUNT(*) FROM perf.QuoteView WHERE CountryCode = 'MT'),
    Q5_CommonCountry_IN  = (SELECT COUNT(*) FROM perf.QuoteView WHERE CountryCode = 'IN');
GO

PRINT 'perf.QuoteView built as a heap. Run 10_index_lab.sql next.';
GO
