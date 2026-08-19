SET NOCOUNT ON;
GO

USE QuotesLab;
GO

IF OBJECT_ID('perf.QuoteView') IS NULL
    THROW 50811, 'perf.QuoteView is missing. Run day8-indexes/index_lab.sql first.', 1;
GO

DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt_Covering ON perf.QuoteView;
DROP INDEX IF EXISTS IX_QuoteView_UserId_ViewedAt          ON perf.QuoteView;
GO

PRINT '=== The non-covering index ===';
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC);
GO

PRINT '--- Rows the lookup plan has to fetch one at a time ---';
SELECT MatchedRows = COUNT(*)
FROM perf.QuoteView
WHERE UserId IN (137, 842, 1699, 2001, 2444);
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

PRINT '=== BEFORE - expect LOOKUP, with Executes = the matched rows ===';
GO

SELECT
    DeviceType,
    CountryCode,
    Views    = COUNT(*),
    AvgDwell = CONVERT(decimal(8,2), AVG(DwellSeconds * 1.0))
FROM perf.QuoteView
WHERE UserId IN (137, 842, 1699, 2001, 2444)
GROUP BY DeviceType, CountryCode
ORDER BY Views DESC, DeviceType, CountryCode
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS IO OFF;
SET STATISTICS PROFILE OFF;
GO

PRINT '=== The covering index ===';
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt_Covering
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DeviceType, CountryCode, DwellSeconds);
GO

SET STATISTICS IO ON;
SET STATISTICS PROFILE ON;
GO

PRINT '=== AFTER - expect no Nested Loops and no LOOKUP, just an Index Seek ===';
GO

SELECT
    DeviceType,
    CountryCode,
    Views    = COUNT(*),
    AvgDwell = CONVERT(decimal(8,2), AVG(DwellSeconds * 1.0))
FROM perf.QuoteView
WHERE UserId IN (137, 842, 1699, 2001, 2444)
GROUP BY DeviceType, CountryCode
ORDER BY Views DESC, DeviceType, CountryCode
OPTION (MAXDOP 1, RECOMPILE);
GO

SET STATISTICS PROFILE OFF;
SET STATISTICS IO OFF;
GO

PRINT '=== What the three included columns cost ===';

SELECT
    IndexName      = i.name,
    ips.index_level,
    ips.page_count,
    AvgRecordBytes = CONVERT(decimal(8,2), ips.avg_record_size_in_bytes)
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('perf.QuoteView'), NULL, NULL, 'DETAILED') AS ips
INNER JOIN sys.indexes AS i
        ON i.object_id = ips.object_id AND i.index_id = ips.index_id
WHERE i.name IN ('IX_QuoteView_UserId_ViewedAt', 'IX_QuoteView_UserId_ViewedAt_Covering')
ORDER BY i.name, ips.index_level;
GO

DROP INDEX IX_QuoteView_UserId_ViewedAt_Covering ON perf.QuoteView;
DROP INDEX IX_QuoteView_UserId_ViewedAt          ON perf.QuoteView;
GO
