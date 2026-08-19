SET NOCOUNT ON;
GO

USE QuotesLab;
GO

IF SCHEMA_ID('perf') IS NULL
    EXEC ('CREATE SCHEMA perf AUTHORIZATION dbo;');
GO

DROP TABLE IF EXISTS perf.QuoteView, perf.WriteHeap, perf.WriteIndexed;
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

PRINT '=== 100,000 rows into a heap ===';

WITH T(x) AS (SELECT x FROM (VALUES (1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) AS v(x)),
Numbers AS
(
    SELECT n = ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1
    FROM T AS a CROSS JOIN T AS b CROSS JOIN T AS c CROSS JOIN T AS d CROSS JOIN T AS e
)
INSERT perf.QuoteView
    (ViewId, QuoteId, UserId, ViewedAt, CountryCode, DeviceType, DwellSeconds, UserAgent, Referrer)
SELECT
    ViewId       = n + 1,
    QuoteId      = 1 + CONVERT(int, SUBSTRING(h, 3, 2)) % 80,
    UserId       = 1 + CONVERT(int, SUBSTRING(h, 5, 2)) % 2500,
    ViewedAt     = DATEADD(MILLISECOND, CONVERT(int, SUBSTRING(h, 1, 2)) % 1000,
                           DATEADD(SECOND, n * 78, CONVERT(datetime2(3), '2026-01-01T00:00:00'))),
    CountryCode  = CASE WHEN CONVERT(int, SUBSTRING(h, 7, 2)) % 10000 > 9996 THEN 'MT'
                        ELSE CHOOSE(1 + CONVERT(int, SUBSTRING(h, 7, 2)) % 10000 / 2000,
                                    'IN', 'IN', 'US', 'GB', 'DE') END,
    DeviceType   = CHOOSE(1 + CONVERT(int, SUBSTRING(h, 9, 2)) % 3, 'mobile', 'desktop', 'tablet'),
    DwellSeconds = 2 + CONVERT(int, SUBSTRING(h, 11, 2)) % 300,
    UserAgent    = 'Mozilla/5.0 ' + REPLICATE('x', 60),
    Referrer     = CASE WHEN CONVERT(int, SUBSTRING(h, 11, 2)) % 5 = 0 THEN NULL
                        ELSE 'https://example.com/quotes/browse?page=2' END
FROM Numbers
CROSS APPLY (SELECT h = HASHBYTES('SHA2_256', CONVERT(varbinary(8), CONVERT(bigint, n)))) AS x;
GO

SET STATISTICS IO ON;
GO

PRINT '=== BEFORE - heap, no indexes ===';
GO

PRINT '--- Q1 one day of views ---';
SELECT ViewCount = COUNT(*), AvgDwell = CONVERT(decimal(8,2), AVG(DwellSeconds * 1.0))
FROM perf.QuoteView
WHERE ViewedAt >= '2026-02-01' AND ViewedAt < '2026-02-02'
OPTION (MAXDOP 1, RECOMPILE);
GO

PRINT '--- Q2 ten most recent views of quote 42 ---';
SELECT TOP (10) QuoteId, ViewedAt, DwellSeconds
FROM perf.QuoteView
WHERE QuoteId = 42
ORDER BY ViewedAt DESC, ViewId DESC
OPTION (MAXDOP 1, RECOMPILE);
GO

PRINT '--- Q3 views from the rare country MT ---';
SELECT ViewId, ViewedAt, QuoteId, DwellSeconds
FROM perf.QuoteView
WHERE CountryCode = 'MT'
ORDER BY ViewedAt
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS IO OFF;
GO

PRINT '=== AFTER 1 - UNIQUE CLUSTERED (ViewedAt, ViewId): expect a range seek ===';
CREATE UNIQUE CLUSTERED INDEX CIX_QuoteView_ViewedAt ON perf.QuoteView (ViewedAt, ViewId);
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

SELECT ViewCount = COUNT(*), AvgDwell = CONVERT(decimal(8,2), AVG(DwellSeconds * 1.0))
FROM perf.QuoteView
WHERE ViewedAt >= '2026-02-01' AND ViewedAt < '2026-02-02'
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS IO OFF;
SET STATISTICS PROFILE OFF;
GO

PRINT '=== AFTER 2 - NONCLUSTERED (QuoteId, ViewedAt DESC, ViewId DESC) INCLUDE (DwellSeconds) ===';
CREATE NONCLUSTERED INDEX IX_QuoteView_QuoteId_ViewedAt
    ON perf.QuoteView (QuoteId, ViewedAt DESC, ViewId DESC) INCLUDE (DwellSeconds);
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

SELECT TOP (10) QuoteId, ViewedAt, DwellSeconds
FROM perf.QuoteView
WHERE QuoteId = 42
ORDER BY ViewedAt DESC, ViewId DESC
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS IO OFF;
SET STATISTICS PROFILE OFF;
GO

PRINT '=== AFTER 3 - NONCLUSTERED (CountryCode): expect a seek plus key lookups ===';
CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode ON perf.QuoteView (CountryCode);
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

SELECT ViewId, ViewedAt, QuoteId, DwellSeconds
FROM perf.QuoteView
WHERE CountryCode = 'MT'
ORDER BY ViewedAt
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS PROFILE OFF;
SET STATISTICS IO OFF;
GO

PRINT '=== The three indexes, and what they cost in space ===';

SELECT
    IndexName = ISNULL(i.name, '(heap)'),
    i.type_desc,
    UsedPages = ps.used_page_count,
    UsedMB    = CONVERT(decimal(8,2), ps.used_page_count * 8.0 / 1024)
FROM sys.indexes AS i
INNER JOIN sys.dm_db_partition_stats AS ps
        ON ps.object_id = i.object_id AND ps.index_id = i.index_id
WHERE i.object_id = OBJECT_ID('perf.QuoteView')
ORDER BY i.index_id;
GO

PRINT '=== Write cost ===';

SELECT TOP (0) * INTO perf.WriteHeap    FROM perf.QuoteView;
SELECT TOP (0) * INTO perf.WriteIndexed FROM perf.QuoteView;
GO

CREATE UNIQUE CLUSTERED INDEX CIX_WriteIndexed ON perf.WriteIndexed (ViewedAt, ViewId);
CREATE NONCLUSTERED INDEX IX_WriteIndexed_QuoteId
    ON perf.WriteIndexed (QuoteId, ViewedAt DESC, ViewId DESC) INCLUDE (DwellSeconds);
CREATE NONCLUSTERED INDEX IX_WriteIndexed_Country ON perf.WriteIndexed (CountryCode);
GO

DECLARE @heap bigint, @indexed bigint;

BEGIN TRANSACTION;
INSERT perf.WriteHeap SELECT TOP (20000) * FROM perf.QuoteView ORDER BY ViewId;
SET @heap = (SELECT database_transaction_log_bytes_used FROM sys.dm_tran_database_transactions
             WHERE transaction_id = CURRENT_TRANSACTION_ID() AND database_id = DB_ID());
COMMIT TRANSACTION;

BEGIN TRANSACTION;
INSERT perf.WriteIndexed SELECT TOP (20000) * FROM perf.QuoteView ORDER BY ViewId;
SET @indexed = (SELECT database_transaction_log_bytes_used FROM sys.dm_tran_database_transactions
                WHERE transaction_id = CURRENT_TRANSACTION_ID() AND database_id = DB_ID());
COMMIT TRANSACTION;

SELECT
    RowsInserted = 20000,
    HeapLogKB    = CONVERT(decimal(12,1), @heap / 1024.0),
    IndexedLogKB = CONVERT(decimal(12,1), @indexed / 1024.0),
    TimesMoreLog = CONVERT(decimal(6,2), @indexed * 1.0 / NULLIF(@heap, 0));
GO
