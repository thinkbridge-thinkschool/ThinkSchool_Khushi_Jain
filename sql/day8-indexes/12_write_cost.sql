/* ============================================================================
   Day 8 — the other side of the trade: what the indexes cost to write
   ----------------------------------------------------------------------------
   The exercise asks for one line on the write-side cost observed. This is the
   measurement behind that line.

   The experiment is four tables with byte-identical DDL, loaded with the same
   20,000 rows in the same twenty batches of a thousand. The only difference
   between them is what is indexed:

       perf.WriteHeap        no index at all
       perf.WriteClustered   UNIQUE CLUSTERED (ViewedAt, ViewId)
       perf.WriteIndexed     the same clustered index plus both non-clustered
                             indexes from 10_index_lab.sql
       perf.WriteRandomKey   UNIQUE CLUSTERED (RowGuid) -- same rows, same
                             count, a clustering key that is not sequential

   The three tables after the first are created with SELECT TOP (0) * INTO from
   the first, so they are identical by construction rather than by three copies
   of a column list that could drift apart.

   Two measures, both of which survive being run on a busy laptop:

     * transaction log bytes, from sys.dm_tran_database_transactions read inside
       the transaction before it commits. Every change to every index is logged,
       so this is close to a direct count of the work index maintenance created.
     * logical reads during the write. An insert has to navigate each index tree
       to find where the row goes, so a write against three structures reads
       three sets of intermediate pages.

   Not elapsed time, for the same reason as 10_index_lab.sql: it is the number
   that feels like evidence and varies by a factor of three between runs.

   Why the loads are batched rather than one INSERT ... SELECT: given the whole
   set at once, the optimiser sorts the rows into clustering-key order and fills
   pages from the left, so even a random key would produce no page splits and
   perf.WriteRandomKey would look free. Twenty batches of a thousand is how an
   application actually writes, and it is the only way the split cost appears.

   Runtime: under a minute.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('perf.WriteLog') IS NULL
BEGIN
    THROW 50802, 'perf.WriteLog is missing. Run 09_build_dataset.sql first.', 1;
END
GO

TRUNCATE TABLE perf.WriteLog;
GO

PRINT '=== 0.  Recovery model, which decides how much log an insert generates ===';

/* Worth printing beside the numbers: under SIMPLE recovery some bulk inserts
   qualify for minimal logging and the log figures below would be measuring a
   different thing. None of the loads here uses TABLOCK, so they are fully
   logged either way, but the reader should not have to take that on trust. */
SELECT
    RecoveryModel = d.recovery_model_desc,
    LogReuseWait  = d.log_reuse_wait_desc
FROM sys.databases AS d
WHERE d.database_id = DB_ID();
GO

/* ============================================================================
   1.  The rows to be written, generated once.
   ----------------------------------------------------------------------------
   The same deterministic derivation as 09_build_dataset.sql, cut to 20,000
   rows, materialised so that all four loads insert the identical set and none
   of them pays for the hashing. Seq is the batching key.

   RowGuid is present in every one of the four tables and is the clustering key
   in only one of them. Carrying the column everywhere keeps the row width
   identical across the four tables, so their page counts are comparable and
   the difference is attributable to the index choice rather than to the schema.
   ============================================================================ */
DROP TABLE IF EXISTS perf.WriteSource;
GO

CREATE TABLE perf.WriteSource
(
    Seq          int              NOT NULL,
    ViewId       bigint           NOT NULL,
    QuoteId      int              NOT NULL,
    UserId       int              NOT NULL,
    ViewedAt     datetime2(3)     NOT NULL,
    CountryCode  char(2)          NOT NULL,
    DeviceType   varchar(10)      NOT NULL,
    DwellSeconds int              NOT NULL,
    UserAgent    varchar(120)     NOT NULL,
    Referrer     varchar(120)     NULL,
    RowGuid      uniqueidentifier NOT NULL,
    CONSTRAINT PK_WriteSource PRIMARY KEY CLUSTERED (Seq)
);
GO

PRINT '=== 1.  Generating the 20,000 rows every load will insert ===';

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
        num.n,
        h.RowHash,
        d1 = CONVERT(int, SUBSTRING(h.RowHash,  1, 2)),
        d2 = CONVERT(int, SUBSTRING(h.RowHash,  3, 2)),
        d3 = CONVERT(int, SUBSTRING(h.RowHash,  5, 2)),
        d4 = CONVERT(int, SUBSTRING(h.RowHash,  7, 2)),
        d5 = CONVERT(int, SUBSTRING(h.RowHash,  9, 2)),
        d6 = CONVERT(int, SUBSTRING(h.RowHash, 11, 2))
    FROM Numbers AS num
    CROSS APPLY (SELECT RowHash = HASHBYTES('SHA2_256', CONVERT(varbinary(8), CONVERT(bigint, num.n)))) AS h
    WHERE num.n < 20000
)
INSERT perf.WriteSource
    (Seq, ViewId, QuoteId, UserId, ViewedAt, CountryCode, DeviceType, DwellSeconds, UserAgent, Referrer, RowGuid)
SELECT
    Seq      = n + 1,
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
    UserAgent = CASE
                    WHEN d5 % 100 < 58 THEN 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) Safari/605.1'
                    WHEN d5 % 100 < 92 THEN 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0 Safari/537.36'
                    ELSE                    'Mozilla/5.0 (Linux; Android 14; Tablet) Chrome/124.0.0.0 Safari/537.36'
                END,
    Referrer = CASE d6 % 5
                   WHEN 0 THEN 'https://example.com/quotes/browse?page=2'
                   WHEN 1 THEN 'https://example.com/collections/favourites'
                   WHEN 2 THEN 'https://example.com/search?q=stoicism'
                   WHEN 3 THEN 'https://example.org/blog/on-notation'
                   ELSE        NULL
               END,

    /* The first 16 bytes of the same hash, read as a uniqueidentifier: a GUID
       that is deterministic but has no relationship to insert order, which is
       exactly the property that makes it a bad clustering key. */
    RowGuid = CONVERT(uniqueidentifier, SUBSTRING(RowHash, 1, 16))
FROM Draws;
GO

/* ============================================================================
   2.  Four tables, identical but for their indexes.
   ============================================================================ */
DROP TABLE IF EXISTS perf.WriteHeap, perf.WriteClustered, perf.WriteIndexed, perf.WriteRandomKey;
GO

CREATE TABLE perf.WriteHeap
(
    ViewId       bigint           NOT NULL,
    QuoteId      int              NOT NULL,
    UserId       int              NOT NULL,
    ViewedAt     datetime2(3)     NOT NULL,
    CountryCode  char(2)          NOT NULL,
    DeviceType   varchar(10)      NOT NULL,
    DwellSeconds int              NOT NULL,
    UserAgent    varchar(120)     NOT NULL,
    Referrer     varchar(120)     NULL,
    RowGuid      uniqueidentifier NOT NULL
);
GO

/* SELECT TOP (0) * INTO copies the column list and its types and nothing else
   -- no constraints, no indexes -- which is precisely the clone this experiment
   needs. It also means the four tables cannot drift apart. */
SELECT TOP (0) * INTO perf.WriteClustered FROM perf.WriteHeap;
SELECT TOP (0) * INTO perf.WriteIndexed   FROM perf.WriteHeap;
SELECT TOP (0) * INTO perf.WriteRandomKey FROM perf.WriteHeap;
GO

CREATE UNIQUE CLUSTERED INDEX CIX_WriteClustered
    ON perf.WriteClustered (ViewedAt, ViewId);
GO

CREATE UNIQUE CLUSTERED INDEX CIX_WriteIndexed
    ON perf.WriteIndexed (ViewedAt, ViewId);
GO

CREATE NONCLUSTERED INDEX IX_WriteIndexed_QuoteId_ViewedAt
    ON perf.WriteIndexed (QuoteId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DwellSeconds);
GO

CREATE NONCLUSTERED INDEX IX_WriteIndexed_CountryCode
    ON perf.WriteIndexed (CountryCode);
GO

/* Sequential key against random key, everything else equal. */
CREATE UNIQUE CLUSTERED INDEX CIX_WriteRandomKey
    ON perf.WriteRandomKey (RowGuid);
GO

/* ============================================================================
   3.  The measurement harness.
   ----------------------------------------------------------------------------
   One statement, or one loop of statements, wrapped in an explicit transaction
   so the log bytes it generated can be read before the commit discards the row
   from sys.dm_tran_database_transactions. SET XACT_ABORT ON so a failure rolls
   back rather than leaving an open transaction holding locks over the rest of
   the script.
   ============================================================================ */
CREATE OR ALTER PROCEDURE perf.MeasureWrite
    @Scenario    varchar(40),
    @Structure   varchar(30),
    @RowsTouched int,
    @Statement   nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @reads0 bigint, @reads1 bigint, @logBytes bigint;

    SET @reads0 = (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);

    BEGIN TRANSACTION;

    EXEC sys.sp_executesql @Statement;

    SET @logBytes = ISNULL((SELECT MAX(t.database_transaction_log_bytes_used)
                            FROM sys.dm_tran_database_transactions AS t
                            WHERE t.transaction_id = CURRENT_TRANSACTION_ID()
                              AND t.database_id    = DB_ID()), 0);

    COMMIT TRANSACTION;

    SET @reads1 = (SELECT r.logical_reads FROM sys.dm_exec_requests AS r WHERE r.session_id = @@SPID);

    INSERT perf.WriteLog (Scenario, Structure, RowsTouched, LogicalReads, LogBytes)
    VALUES (@Scenario, @Structure, @RowsTouched, @reads1 - @reads0, @logBytes);
END
GO

/* Twenty batches of a thousand into whichever table is named. The table name is
   split and re-quoted rather than concatenated raw: it is the only part of this
   statement that is not a literal, and a lab script is no reason to write the
   injectable version of it. */
CREATE OR ALTER PROCEDURE perf.LoadInBatches
    @TargetTable sysname,
    @Scenario    varchar(40),
    @Structure   varchar(30)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @target nvarchar(300) =
        QUOTENAME(PARSENAME(@TargetTable, 2)) + N'.' + QUOTENAME(PARSENAME(@TargetTable, 1));

    /* The leading literal is widened to nvarchar(max) before any concatenation.
       Without that, a chain of nvarchar literals is typed nvarchar(4000) and a
       longer statement would be silently truncated -- this one is nowhere near
       the limit, but the failure mode is a syntax error a long way from its
       cause. */
    DECLARE @statement nvarchar(max) = CONVERT(nvarchar(max), N'
DECLARE @batch int = 0;
WHILE @batch < 20
BEGIN
    INSERT ') + @target + N'
        (ViewId, QuoteId, UserId, ViewedAt, CountryCode, DeviceType, DwellSeconds, UserAgent, Referrer, RowGuid)
    SELECT
        s.ViewId, s.QuoteId, s.UserId, s.ViewedAt, s.CountryCode,
        s.DeviceType, s.DwellSeconds, s.UserAgent, s.Referrer, s.RowGuid
    FROM perf.WriteSource AS s
    WHERE s.Seq >  @batch * 1000
      AND s.Seq <= @batch * 1000 + 1000;

    SET @batch += 1;
END';

    EXEC perf.MeasureWrite
         @Scenario    = @Scenario,
         @Structure   = @Structure,
         @RowsTouched = 20000,
         @Statement   = @statement;
END
GO

/* ============================================================================
   4.  Load all four.
   ============================================================================ */
PRINT '=== 4.  Loading 20,000 rows into each of the four structures ===';

EXEC perf.LoadInBatches 'perf.WriteHeap',       'insert 20,000 rows', 'heap';
EXEC perf.LoadInBatches 'perf.WriteClustered',  'insert 20,000 rows', 'clustered only';
EXEC perf.LoadInBatches 'perf.WriteIndexed',    'insert 20,000 rows', 'clustered + 2 NC';
EXEC perf.LoadInBatches 'perf.WriteRandomKey',  'insert 20,000 rows', 'clustered on a GUID';
GO

/* ============================================================================
   5.  Updates, which are where indexes get expensive in an unobvious way.
   ----------------------------------------------------------------------------
   An update to a column no index mentions touches one structure. An update to a
   column that is an index *key* is a delete and an insert in that index, because
   the row has to move to keep the index in order. The two statements below
   differ only in which column they set.

   Both predicates filter on ViewId, which leads no index on either table, so
   both plans scan to find their rows and the read side of the comparison stays
   fair. What differs is purely the maintenance.
   ============================================================================ */
PRINT '=== 5.  Updating 2,000 rows: a column in no index, then an index key ===';

EXEC perf.MeasureWrite
     @Scenario    = 'update column in no index',
     @Structure   = 'heap',
     @RowsTouched = 2000,
     @Statement   = N'UPDATE perf.WriteHeap SET UserId = UserId + 1 WHERE ViewId <= 2000;';

EXEC perf.MeasureWrite
     @Scenario    = 'update column in no index',
     @Structure   = 'clustered + 2 NC',
     @RowsTouched = 2000,
     @Statement   = N'UPDATE perf.WriteIndexed SET UserId = UserId + 1 WHERE ViewId <= 2000;';

EXEC perf.MeasureWrite
     @Scenario    = 'update an index key column',
     @Structure   = 'heap',
     @RowsTouched = 2000,
     @Statement   = N'UPDATE perf.WriteHeap SET CountryCode = ''ZZ'' WHERE ViewId <= 2000;';

EXEC perf.MeasureWrite
     @Scenario    = 'update an index key column',
     @Structure   = 'clustered + 2 NC',
     @RowsTouched = 2000,
     @Statement   = N'UPDATE perf.WriteIndexed SET CountryCode = ''ZZ'' WHERE ViewId <= 2000;';
GO

/* ============================================================================
   6.  Results.
   ============================================================================ */
PRINT '=== 6a.  Log bytes and logical reads per scenario ===';

/* VsHeap is the ratio against the same scenario run on the unindexed table, so
   it reads directly as "this many times the write cost for the same change". */
SELECT
    w.Scenario,
    w.Structure,
    w.RowsTouched,
    w.LogicalReads,
    LogKB          = CONVERT(decimal(12,1), w.LogBytes / 1024.0),
    LogBytesPerRow = CONVERT(decimal(10,1), w.LogBytes * 1.0 / NULLIF(w.RowsTouched, 0)),
    VsHeap         = CONVERT(decimal(6,2), w.LogBytes * 1.0 / NULLIF(h.LogBytes, 0))
FROM perf.WriteLog AS w
OUTER APPLY (
    SELECT LogBytes = MIN(x.LogBytes)
    FROM perf.WriteLog AS x
    WHERE x.Scenario  = w.Scenario
      AND x.Structure = 'heap'
) AS h
ORDER BY w.Seq;
GO

PRINT '=== 6b.  Index rows written, and leaf pages allocated ===';

/* leaf_insert_count is the direct answer to "how many index rows did those
   20,000 table rows become". For perf.WriteIndexed it should read 20,000 three
   times over: the row itself, plus an entry in each non-clustered index.

   leaf_allocation_count is the page-split proxy. An ever-increasing key
   allocates a new page only when the last one fills, so the figure lands near
   the table's page count. A random key allocates on every split as well, so it
   runs far above it -- each split moving half a page of rows and logging the
   move. */
SELECT
    TableName        = OBJECT_SCHEMA_NAME(os.object_id) + '.' + OBJECT_NAME(os.object_id),
    IndexName        = ISNULL(i.name, '(heap)'),
    IndexType        = i.type_desc,
    LeafInserts      = os.leaf_insert_count,
    LeafUpdates      = os.leaf_update_count,
    LeafDeletes      = os.leaf_delete_count,
    LeafAllocations  = os.leaf_allocation_count
FROM (VALUES ('perf.WriteHeap'),
             ('perf.WriteClustered'),
             ('perf.WriteIndexed'),
             ('perf.WriteRandomKey')) AS v(TableName)
CROSS APPLY sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID(v.TableName), NULL, NULL) AS os
INNER JOIN sys.indexes AS i
        ON i.object_id = os.object_id
       AND i.index_id  = os.index_id
ORDER BY v.TableName, os.index_id;
GO

PRINT '=== 6c.  Space: every structure, and the total per table ===';

SELECT
    TableName = s.name + '.' + t.name,
    IndexName = ISNULL(i.name, '(heap)'),
    IndexType = i.type_desc,
    TotalRows = ps.row_count,
    UsedPages = ps.used_page_count,
    UsedKB    = ps.used_page_count * 8
FROM sys.dm_db_partition_stats AS ps
INNER JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
INNER JOIN sys.tables  AS t ON t.object_id = ps.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE s.name = 'perf'
  AND t.name IN ('WriteHeap', 'WriteClustered', 'WriteIndexed', 'WriteRandomKey')
ORDER BY t.name, ps.index_id;
GO

PRINT '=== 6d.  Total pages per table -- the storage cost of the indexes ===';

SELECT
    TableName  = s.name + '.' + t.name,
    Structures = COUNT(*),
    TotalPages = SUM(ps.used_page_count),
    TotalKB    = SUM(ps.used_page_count) * 8
FROM sys.dm_db_partition_stats AS ps
INNER JOIN sys.tables  AS t ON t.object_id = ps.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE s.name = 'perf'
  AND t.name IN ('WriteHeap', 'WriteClustered', 'WriteIndexed', 'WriteRandomKey')
GROUP BY s.name, t.name
ORDER BY TotalPages DESC;
GO

PRINT '=== 6e.  Fragmentation: a sequential clustering key against a GUID ===';

/* The comparison the GUID table exists for. Same 20,000 rows, same width, same
   number of batches. Expect the sequential key near 0% fragmented and around
   99% page fullness, and the GUID key heavily fragmented at roughly 70%
   fullness -- which means it needs about 40% more pages to hold the same data,
   and every read of it pays that surcharge forever.

   avg_page_space_used_in_percent is the figure that matters for reads:
   fragmentation slows a scan down, but poor page fullness makes the scan bigger.
   The two indexes on perf.WriteIndexed appear here too, and they show the same
   effect in miniature -- IX_..._CountryCode is keyed on a column with no
   relationship to insert order, so it splits as well. */
SELECT
    TableName          = OBJECT_SCHEMA_NAME(ips.object_id) + '.' + OBJECT_NAME(ips.object_id),
    IndexName          = ISNULL(i.name, '(heap)'),
    IndexType          = ips.index_type_desc,
    ips.page_count,
    AvgPageFullnessPct = CONVERT(decimal(5,2), ips.avg_page_space_used_in_percent),
    FragmentationPct   = CONVERT(decimal(5,2), ips.avg_fragmentation_in_percent)
FROM (VALUES ('perf.WriteHeap'),
             ('perf.WriteClustered'),
             ('perf.WriteIndexed'),
             ('perf.WriteRandomKey')) AS v(TableName)
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID(v.TableName), NULL, NULL, 'DETAILED') AS ips
INNER JOIN sys.indexes AS i
        ON i.object_id = ips.object_id
       AND i.index_id  = ips.index_id
WHERE ips.index_level = 0
ORDER BY v.TableName, ips.index_id;
GO

PRINT 'Write-cost measurement complete.';
GO
