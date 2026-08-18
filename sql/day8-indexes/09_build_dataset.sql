/* ============================================================================
   Day 8 — the dataset the index measurements run against
   ----------------------------------------------------------------------------
   Builds perf.QuoteView: 100,000 rows of synthetic view-log events, created as
   a HEAP with no index of any kind. 
   */
SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


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

   
    QuoteId  = 1 + d2 % 80,

    UserId   = 1 + d3 % 2500,


    ViewedAt = DATEADD(MILLISECOND, d1 % 1000,
                       DATEADD(SECOND, n * 78, CONVERT(datetime2(3), '2026-01-01T00:00:00'))),

   
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
   ============================================================================ */
PRINT '=== 3.  Column statistics (FULLSCAN, before any measurement) ===';

CREATE STATISTICS ST_QuoteView_ViewedAt    ON perf.QuoteView (ViewedAt)    WITH FULLSCAN;
CREATE STATISTICS ST_QuoteView_QuoteId     ON perf.QuoteView (QuoteId)     WITH FULLSCAN;
CREATE STATISTICS ST_QuoteView_CountryCode ON perf.QuoteView (CountryCode) WITH FULLSCAN;
GO

/* ============================================================================
   4.  What was built
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
