SET NOCOUNT ON;
GO

USE QuotesLab;
GO

DECLARE @deadline datetime2(3) = DATEADD(SECOND, 90, SYSUTCDATETIME());
WHILE OBJECT_ID('txn.WaitForSignal') IS NULL
BEGIN
    IF SYSUTCDATETIME() > @deadline
        THROW 50913, 'The txn harness never appeared.', 1;
    WAITFOR DELAY '00:00:00.100';
END
GO

SET DEADLOCK_PRIORITY LOW;
GO

PRINT '=== 1. The deadlock: this session takes row 2, then row 1 ===';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 2;
    PRINT '--- Holding row 2 ---';

    EXEC txn.RaiseSignal 'dl-b-holds-2';
    EXEC txn.WaitForSignal 'dl-a-holds-1';

    PRINT '--- Now asking for row 1, which session A holds ---';
    UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 1;

    COMMIT TRANSACTION;
    PRINT '--- Committed, which means no deadlock happened ---';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    PRINT '--- The victim message ---';
    SELECT
        ErrorNumber   = ERROR_NUMBER(),
        ErrorSeverity = ERROR_SEVERITY(),
        ErrorMessage  = ERROR_MESSAGE();
END CATCH
GO

PRINT '';
PRINT '=== 2. The fix: both sessions take row 1 first, then row 2 ===';
GO

EXEC txn.WaitForSignal 'dl-fixed-a-holds-1';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    EXEC txn.RaiseSignal 'dl-fixed-b-waiting';

    PRINT '--- Asking for row 1 first, so this waits rather than deadlocking ---';
    UPDATE txn.QuoteStat SET LikeCount = LikeCount + 1 WHERE QuoteId = 1;
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

EXEC txn.RaiseSignal 'dl-fixed-b-done';
GO
