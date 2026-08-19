SET NOCOUNT ON;
GO

USE QuotesLab;
GO

DECLARE @deadline datetime2(3) = DATEADD(SECOND, 90, SYSUTCDATETIME());
WHILE OBJECT_ID('txn.WaitForSignal') IS NULL
BEGIN
    IF SYSUTCDATETIME() > @deadline
        THROW 50902, 'Session A did not create the lab objects.', 1;
    WAITFOR DELAY '00:00:00.100';
END
GO

PRINT '=== 1. Dirty read at READ UNCOMMITTED ===';
GO

BEGIN TRANSACTION;

UPDATE txn.QuoteStat SET LikeCount = 111 WHERE QuoteId = 1;
PRINT '--- Row 1 set to 111 and left uncommitted ---';

EXEC txn.RaiseSignal 'b-updated-uncommitted';
EXEC txn.WaitForSignal 'a-read-dirty-value';

ROLLBACK TRANSACTION;
PRINT '--- Rolled back ---';

EXEC txn.RaiseSignal 'b-rolled-back';
GO

PRINT '';
PRINT '=== 2. READ COMMITTED prevents the dirty read ===';
GO

EXEC txn.WaitForSignal 'a-saw-rollback';
GO

BEGIN TRANSACTION;

UPDATE txn.QuoteStat SET LikeCount = 112 WHERE QuoteId = 1;
PRINT '--- Row 1 set to 112 and left uncommitted ---';

EXEC txn.RaiseSignal 'b-updated-uncommitted-again';
EXEC txn.WaitForSignal 'a-about-to-read';
WAITFOR DELAY '00:00:03';

PRINT '--- Session A is waiting on the exclusive lock this transaction holds ---';
EXEC txn.ShowBlockedSessions;

ROLLBACK TRANSACTION;
PRINT '--- Rolled back, so session A reads 100 rather than 112 ---';
GO

PRINT '';
PRINT '=== 3. Non-repeatable read at READ COMMITTED ===';
GO

EXEC txn.WaitForSignal 'a-read-row-2-once';
GO

UPDATE txn.QuoteStat SET LikeCount = 222 WHERE QuoteId = 2;
PRINT '--- Row 2 set to 222 and committed while session A''s transaction is open ---';
EXEC txn.RaiseSignal 'b-committed-row-2';
GO

PRINT '';
PRINT '=== 4. REPEATABLE READ prevents the non-repeatable read ===';
GO

EXEC txn.WaitForSignal 'a-holding-row-2';
EXEC txn.RaiseSignal 'b-updating-row-2';
GO

PRINT '--- This update blocks until session A commits ---';
UPDATE txn.QuoteStat SET LikeCount = 999 WHERE QuoteId = 2;
PRINT '--- Row 2 set to 999 once the lock was released ---';
EXEC txn.RaiseSignal 'b-updated-row-2';
GO

PRINT '';
PRINT '=== 5. Phantom read at REPEATABLE READ ===';
GO

EXEC txn.WaitForSignal 'a-counted-range-once';
GO

INSERT txn.QuoteStat (QuoteId, LikeCount) VALUES (11, 1100);
PRINT '--- QuoteId 11 inserted into the range session A is reading ---';
EXEC txn.RaiseSignal 'b-inserted-into-range';
GO

PRINT '';
PRINT '=== 6. SERIALIZABLE prevents the phantom read ===';
GO

EXEC txn.WaitForSignal 'a-locked-range';
EXEC txn.RaiseSignal 'b-inserting-into-range';
GO

PRINT '--- This insert blocks until session A commits ---';
INSERT txn.QuoteStat (QuoteId, LikeCount) VALUES (12, 1200);
PRINT '--- QuoteId 12 inserted once the range lock was released ---';
EXEC txn.RaiseSignal 'b-inserted-after-commit';
GO
