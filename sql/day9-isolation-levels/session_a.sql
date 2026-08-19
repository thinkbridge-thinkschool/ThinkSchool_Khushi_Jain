SET NOCOUNT ON;
GO

USE QuotesLab;
GO

IF SCHEMA_ID('txn') IS NULL
    EXEC ('CREATE SCHEMA txn AUTHORIZATION dbo;');
GO

DROP PROCEDURE IF EXISTS txn.WaitForSignal, txn.RaiseSignal, txn.ShowBlockedSessions;
DROP TABLE IF EXISTS txn.Signal, txn.QuoteStat;
GO

CREATE TABLE txn.QuoteStat
(
    QuoteId   int NOT NULL,
    LikeCount int NOT NULL,
    CONSTRAINT PK_QuoteStat PRIMARY KEY CLUSTERED (QuoteId)
);
GO

INSERT txn.QuoteStat (QuoteId, LikeCount) VALUES (1, 100), (2, 200), (10, 1000);
GO

CREATE TABLE txn.Signal (Name varchar(40) NOT NULL);
GO

CREATE PROCEDURE txn.RaiseSignal @Name varchar(40)
AS
    SET NOCOUNT ON;
    INSERT txn.Signal (Name) VALUES (@Name);
GO

CREATE PROCEDURE txn.WaitForSignal @Name varchar(40)
AS
    SET NOCOUNT ON;
    DECLARE @deadline datetime2(3) = DATEADD(SECOND, 90, SYSUTCDATETIME());
    WHILE NOT EXISTS (SELECT 1 FROM txn.Signal WITH (NOLOCK) WHERE Name = @Name)
    BEGIN
        IF SYSUTCDATETIME() > @deadline
            THROW 50901, 'Timed out waiting for the other session.', 1;
        WAITFOR DELAY '00:00:00.100';
    END
GO

CREATE PROCEDURE txn.ShowBlockedSessions
AS
    SET NOCOUNT ON;
    SELECT
        BlockedSessions = COUNT(*),
        WaitTypes       = ISNULL(STRING_AGG(r.wait_type, ', '), '(none)')
    FROM sys.dm_exec_requests AS r
    WHERE r.blocking_session_id <> 0
      AND r.session_id > 50
      AND r.session_id <> @@SPID;
GO

PRINT '=== Snapshot options: both must be off, or these locks never happen ===';
SELECT
    ReadCommittedSnapshot  = is_read_committed_snapshot_on,
    SnapshotIsolationState = snapshot_isolation_state_desc
FROM sys.databases
WHERE name = 'QuotesLab';
GO

PRINT '';
PRINT '=== 1. Dirty read at READ UNCOMMITTED ===';
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
GO

EXEC txn.WaitForSignal 'b-updated-uncommitted';
GO

PRINT '--- Row 1 while session B holds an uncommitted UPDATE ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 1;
GO

EXEC txn.RaiseSignal 'a-read-dirty-value';
EXEC txn.WaitForSignal 'b-rolled-back';
GO

PRINT '--- Row 1 after session B rolled back: the value above never existed ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 1;
GO

EXEC txn.RaiseSignal 'a-saw-rollback';
GO

PRINT '';
PRINT '=== 2. READ COMMITTED prevents the dirty read ===';
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
GO

EXEC txn.WaitForSignal 'b-updated-uncommitted-again';
EXEC txn.RaiseSignal 'a-about-to-read';
GO

PRINT '--- This read blocks until session B rolls back, then returns 100 ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 1;
GO

PRINT '';
PRINT '=== 3. Non-repeatable read at READ COMMITTED ===';
GO

BEGIN TRANSACTION;

PRINT '--- First read of row 2 ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 2;

EXEC txn.RaiseSignal 'a-read-row-2-once';
EXEC txn.WaitForSignal 'b-committed-row-2';

PRINT '--- Second read of row 2, same transaction ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 2;

COMMIT TRANSACTION;
GO

PRINT '';
PRINT '=== 4. REPEATABLE READ prevents the non-repeatable read ===';
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
GO

BEGIN TRANSACTION;

PRINT '--- First read of row 2 ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 2;

EXEC txn.RaiseSignal 'a-holding-row-2';
EXEC txn.WaitForSignal 'b-updating-row-2';
WAITFOR DELAY '00:00:03';

PRINT '--- Session B is waiting on the shared lock this transaction still holds ---';
EXEC txn.ShowBlockedSessions;

PRINT '--- Second read of row 2, same transaction ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 2;

COMMIT TRANSACTION;
GO

EXEC txn.WaitForSignal 'b-updated-row-2';
GO

PRINT '--- Row 2 after the commit released the lock ---';
SELECT QuoteId, LikeCount FROM txn.QuoteStat WHERE QuoteId = 2;
GO

PRINT '';
PRINT '=== 5. Phantom read at REPEATABLE READ ===';
GO

BEGIN TRANSACTION;

PRINT '--- First count of QuoteId 10 to 19 ---';
SELECT RowsInRange = COUNT(*) FROM txn.QuoteStat WHERE QuoteId BETWEEN 10 AND 19;

EXEC txn.RaiseSignal 'a-counted-range-once';
EXEC txn.WaitForSignal 'b-inserted-into-range';

PRINT '--- Second count of the same range, same transaction ---';
SELECT RowsInRange = COUNT(*) FROM txn.QuoteStat WHERE QuoteId BETWEEN 10 AND 19;

COMMIT TRANSACTION;
GO

PRINT '';
PRINT '=== 6. SERIALIZABLE prevents the phantom read ===';
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
GO

BEGIN TRANSACTION;

PRINT '--- First count of QuoteId 10 to 19 ---';
SELECT RowsInRange = COUNT(*) FROM txn.QuoteStat WHERE QuoteId BETWEEN 10 AND 19;

EXEC txn.RaiseSignal 'a-locked-range';
EXEC txn.WaitForSignal 'b-inserting-into-range';
WAITFOR DELAY '00:00:03';

PRINT '--- Session B is waiting on the key-range lock this transaction holds ---';
EXEC txn.ShowBlockedSessions;

PRINT '--- Second count of the same range, same transaction ---';
SELECT RowsInRange = COUNT(*) FROM txn.QuoteStat WHERE QuoteId BETWEEN 10 AND 19;

COMMIT TRANSACTION;
GO

SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
EXEC txn.WaitForSignal 'b-inserted-after-commit';
GO

PRINT '--- The range after the commit released it ---';
SELECT RowsInRange = COUNT(*) FROM txn.QuoteStat WHERE QuoteId BETWEEN 10 AND 19;
GO
