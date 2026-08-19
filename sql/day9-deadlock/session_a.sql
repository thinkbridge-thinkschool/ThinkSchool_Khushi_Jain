SET NOCOUNT ON;
GO

USE QuotesLab;
GO

IF OBJECT_ID('txn.WaitForSignal') IS NULL
    THROW 50911, 'The txn harness is missing. Run day9-isolation-levels/session_a.sql first.', 1;
GO

DELETE FROM txn.Signal WHERE Name LIKE 'dl-%';
GO

PRINT '=== 1. The deadlock: this session takes row 1, then row 2 ===';

DECLARE @startedAt datetime2(3) = SYSUTCDATETIME();

BEGIN TRANSACTION;

UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 1;
PRINT '--- Holding row 1 ---';

EXEC txn.RaiseSignal 'dl-a-holds-1';
EXEC txn.WaitForSignal 'dl-b-holds-2';

PRINT '--- Now asking for row 2, which session B holds ---';
UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 2;

COMMIT TRANSACTION;
PRINT '--- This session survived: session B was chosen as the victim ---';

WAITFOR DELAY '00:00:03';

PRINT '=== The deadlock graph, from the always-on system_health session ===';

DECLARE @graphXml xml =
(
    SELECT TOP (1) e.x.query('(data/value/deadlock)[1]')
    FROM
    (
        SELECT TargetData = CONVERT(xml, st.target_data)
        FROM sys.dm_xe_session_targets AS st
        INNER JOIN sys.dm_xe_sessions AS s
                ON s.address = st.event_session_address
        WHERE s.name = 'system_health'
          AND st.target_name = 'ring_buffer'
    ) AS rb
    CROSS APPLY rb.TargetData.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS e(x)
    WHERE e.x.value('@timestamp', 'datetime2(3)') > @startedAt
    ORDER BY e.x.value('@timestamp', 'datetime2(3)') DESC
);

IF @graphXml IS NULL
    THROW 50912, 'No deadlock graph was recorded after this run started.', 1;

SET @graphXml.modify('delete //stackFrames');
SET @graphXml.modify('delete //executionStack');
SET @graphXml.modify('delete //inputbuf');

DECLARE @graph nvarchar(max) = CONVERT(nvarchar(max), @graphXml);

DECLARE @offset int = 1;
WHILE @offset <= LEN(@graph)
BEGIN
    PRINT SUBSTRING(@graph, @offset, 800);
    SET @offset += 800;
END
GO

PRINT '';
PRINT '=== 2. The fix: both sessions take row 1 first, then row 2 ===';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 1;
    PRINT '--- Holding row 1 ---';

    EXEC txn.RaiseSignal 'dl-fixed-a-holds-1';
    EXEC txn.WaitForSignal 'dl-fixed-b-waiting';
    WAITFOR DELAY '00:00:03';

    PRINT '--- Session B is queued behind row 1, holding nothing this session needs ---';
    EXEC txn.ShowBlockedSessions;

    UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 2;

    COMMIT TRANSACTION;
    PRINT '--- Committed, no deadlock ---';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    SELECT ErrorNumber = ERROR_NUMBER(), ErrorMessage = ERROR_MESSAGE();
END CATCH
GO

EXEC txn.WaitForSignal 'dl-fixed-b-done';
GO

PRINT '--- Both rows after both transactions committed ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId IN (1, 2) ORDER BY QuoteId;
GO
