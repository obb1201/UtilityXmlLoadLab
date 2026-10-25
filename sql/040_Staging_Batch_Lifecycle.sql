-- 040: Procedures shared by both approaches: start a batch, finalize its evidence, reset staging.
-- Both approach A (T-SQL) and approach B (.NET) call usp_BeginBatch and usp_FinalizeBatch inside
-- their batch transaction, so the evidence is built by the same code for both.
-- Per-batch statements use MAXDOP 1: batches run side by side, and parallel plans here caused
-- deadlocks (exchange events) without making a 1,000-message batch any faster.
-- Safe to run more than once.
USE SapStaging;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_BeginBatch: clears earlier staging output of the batch so it can be (re)loaded.
-- Refuses when the batch is already Approved or Promoted. Must run inside the caller's transaction.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_BeginBatch
    @BatchId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Inbox WHERE BatchId = @BatchId)
    BEGIN
        THROW 50010, 'Batch has no messages in the Inbox.', 1;
    END;

    -- One session per batch. An application lock (not a key-range lock) so that loading
    -- different batches in parallel never blocks; it is released when the transaction ends.
    DECLARE @LockResult int;
    DECLARE @LockName nvarchar(255) = CONCAT(N'UtilityXmlLoadLab.Batch.', @BatchId);
    EXEC @LockResult = sp_getapplock @Resource = @LockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;

    IF @LockResult < 0
    BEGIN
        THROW 50012, 'This batch is being loaded by another session.', 1;
    END;

    IF EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch
               WHERE BatchId = @BatchId AND Status IN ('Approved', 'Promoted'))
    BEGIN
        THROW 50011, 'Batch is already Approved or Promoted and cannot be reloaded.', 1;
    END;

    DELETE FROM stg.MessageHeader   WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Customer        WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.CustomerAddress WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Account         WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Premise         WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Installation    WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Meter           WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.MeterRegister   WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Contract        WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Agreement       WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.MoveInOut       WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM stg.Payment         WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM dbo.LoadEvidence_Message WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM dbo.LoadEvidence_Finding WHERE BatchId = @BatchId OPTION (MAXDOP 1);
    DELETE FROM dbo.LoadEvidence_Batch   WHERE BatchId = @BatchId OPTION (MAXDOP 1);
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_FinalizeBatch: called after an approach has written its staging rows and one evidence
-- row per message (ArrivalNo = 1). It:
--   1. records every duplicate arrival of the batch's messages,
--   2. counts the rows really present in staging per message and checks them against the
--      expected count,
--   3. proves every Inbox message of the batch has evidence (throws if not),
--   4. updates Inbox.Status and writes the LoadEvidence_Batch row (Status = Staged).
-- Must run inside the caller's transaction.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_FinalizeBatch
    @BatchId  int,
    @Approach char(1)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 1. Duplicate arrivals. Same content = harmless resend. Different content = Failed,
    --    because the sender sent two versions under one MessageId and only the first was loaded.
    INSERT INTO dbo.LoadEvidence_Message
        (MessageId, ArrivalNo, BatchId, MessageType, ReceivedAt, RawXmlHash, ExpectedRowCount,
         StagedRowCount, ValidationStatus, ErrorDetail, Approach)
    SELECT d.MessageId,
           1 + ROW_NUMBER() OVER (PARTITION BY d.MessageId ORDER BY d.DuplicateId),
           @BatchId,
           e.MessageType,
           d.ReceivedAt,
           x.RawXmlHash,
           NULL,
           0,
           CASE WHEN x.RawXmlHash = e.RawXmlHash THEN 'Duplicate' ELSE 'Failed' END,
           CASE WHEN x.RawXmlHash = e.RawXmlHash
                THEN N'Duplicate arrival with identical content; skipped (idempotent).'
                ELSE N'Duplicate MessageId with DIFFERENT content; this version was not loaded.'
           END,
           @Approach
    FROM dbo.InboxDuplicate d
    JOIN dbo.LoadEvidence_Message e WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
        ON e.MessageId = d.MessageId AND e.ArrivalNo = 1 AND e.BatchId = @BatchId
    CROSS APPLY (SELECT HASHBYTES('SHA2_256', d.RawXml) AS RawXmlHash) x
    OPTION (MAXDOP 1);

    -- Evidence and Inbox rows are always reached through their BatchId index, so a batch never
    -- touches (or waits for) another batch's uncommitted rows. Without the hints, parallel batches
    -- deadlocked: the optimizer preferred the MessageId keys, which run across all batches.

    -- 2. Rows really in staging, per message.
    WITH Staged AS
    (
        SELECT MessageId FROM stg.MessageHeader   WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Customer        WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.CustomerAddress WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Account         WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Premise         WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Installation    WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Meter           WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.MeterRegister   WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Contract        WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Agreement       WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.MoveInOut       WHERE BatchId = @BatchId
        UNION ALL SELECT MessageId FROM stg.Payment         WHERE BatchId = @BatchId
    ),
    StagedCount AS
    (
        SELECT MessageId, COUNT(*) AS RowCnt FROM Staged GROUP BY MessageId
    )
    UPDATE e
    SET StagedRowCount = ISNULL(s.RowCnt, 0)
    FROM dbo.LoadEvidence_Message e WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
    LEFT JOIN StagedCount s ON s.MessageId = e.MessageId
    WHERE e.BatchId = @BatchId AND e.ArrivalNo = 1
    OPTION (MAXDOP 1);

    -- Staged rows from a message that is not Passed means an approach bug: refuse the batch.
    IF EXISTS (SELECT 1 FROM dbo.LoadEvidence_Message WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
               WHERE BatchId = @BatchId AND ArrivalNo = 1
                 AND ValidationStatus <> 'Passed' AND StagedRowCount > 0)
    BEGIN
        THROW 50020, 'A failed message has rows in staging. The batch was rolled back.', 1;
    END;

    -- Passed, but staging does not hold exactly the expected rows: mark Failed with the reason.
    UPDATE e
    SET ValidationStatus = 'Failed',
        ErrorDetail = LEFT(CONCAT(N'Staged row count ', StagedRowCount, N' does not match expected ',
                                  ExpectedRowCount, N'.'), 4000)
    FROM dbo.LoadEvidence_Message e WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
    WHERE BatchId = @BatchId AND ArrivalNo = 1
      AND ValidationStatus = 'Passed'
      AND StagedRowCount <> ISNULL(ExpectedRowCount, -1)
    OPTION (MAXDOP 1);

    -- 3. Completeness: every Inbox message of the batch has exactly one evidence row.
    DECLARE @InboxCount int = (SELECT COUNT(*) FROM dbo.Inbox WHERE BatchId = @BatchId);
    DECLARE @EvidenceCount int =
        (SELECT COUNT(*) FROM dbo.LoadEvidence_Message e WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
         JOIN dbo.Inbox i ON i.MessageId = e.MessageId AND i.BatchId = @BatchId
         WHERE e.BatchId = @BatchId AND e.ArrivalNo = 1);

    IF @InboxCount <> @EvidenceCount
    BEGIN
        DECLARE @Text nvarchar(200) =
            CONCAT(N'Evidence incomplete: ', @InboxCount, N' messages in Inbox, ', @EvidenceCount, N' evidence rows.');
        THROW 50021, @Text, 1;
    END;

    -- 4. Inbox status and the batch evidence row.
    UPDATE i
    SET Status = e.ValidationStatus
    FROM dbo.Inbox i WITH (INDEX(IX_Inbox_BatchId))
    JOIN dbo.LoadEvidence_Message e WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
        ON e.MessageId = i.MessageId AND e.ArrivalNo = 1 AND e.BatchId = @BatchId
    WHERE i.BatchId = @BatchId
    OPTION (MAXDOP 1);

    INSERT INTO dbo.LoadEvidence_Batch
        (BatchId, MessageCount, PassedCount, FailedCount, DuplicateCount, StagedRowTotal, Status, Approach, StagedAt)
    SELECT @BatchId,
           @InboxCount,
           SUM(CASE WHEN ValidationStatus = 'Passed' THEN 1 ELSE 0 END),
           SUM(CASE WHEN ValidationStatus = 'Failed' THEN 1 ELSE 0 END),
           SUM(CASE WHEN ValidationStatus = 'Duplicate' THEN 1 ELSE 0 END),
           SUM(StagedRowCount),
           'Staged',
           @Approach,
           SYSUTCDATETIME()
    FROM dbo.LoadEvidence_Message WITH (INDEX(CIX_LoadEvidence_Message_BatchId))
    WHERE BatchId = @BatchId
    OPTION (MAXDOP 1);
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ResetStaging: empties staging and evidence (Inbox and batches stay), for benchmarks.
-- Refuses when any batch was promoted, unless @AlsoResetTarget = 1 (which also empties SapTarget).
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ResetStaging
    @AlsoResetTarget bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @AlsoResetTarget = 0 AND EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch WHERE Status = 'Promoted')
    BEGIN
        THROW 50030, 'Some batches are already promoted. Use @AlsoResetTarget = 1 to reset the target too.', 1;
    END;

    IF @AlsoResetTarget = 1
    BEGIN
        -- Children first, because of the foreign keys.
        DELETE FROM SapTarget.dbo.Payment;
        DELETE FROM SapTarget.dbo.MoveInOut;
        DELETE FROM SapTarget.dbo.Agreement;
        DELETE FROM SapTarget.dbo.Contract;
        DELETE FROM SapTarget.dbo.MeterRegister;
        DELETE FROM SapTarget.dbo.MeterInstall;
        DELETE FROM SapTarget.dbo.Meter;
        DELETE FROM SapTarget.dbo.Installation;
        DELETE FROM SapTarget.dbo.Premise;
        DELETE FROM SapTarget.dbo.Account;
        DELETE FROM SapTarget.dbo.CustomerAddress;
        DELETE FROM SapTarget.dbo.Customer;
    END;

    TRUNCATE TABLE stg.MessageHeader;
    TRUNCATE TABLE stg.Customer;
    TRUNCATE TABLE stg.CustomerAddress;
    TRUNCATE TABLE stg.Account;
    TRUNCATE TABLE stg.Premise;
    TRUNCATE TABLE stg.Installation;
    TRUNCATE TABLE stg.Meter;
    TRUNCATE TABLE stg.MeterRegister;
    TRUNCATE TABLE stg.Contract;
    TRUNCATE TABLE stg.Agreement;
    TRUNCATE TABLE stg.MoveInOut;
    TRUNCATE TABLE stg.Payment;
    TRUNCATE TABLE dbo.LoadEvidence_Message;
    TRUNCATE TABLE dbo.LoadEvidence_Batch;
    TRUNCATE TABLE dbo.LoadEvidence_Finding;
    TRUNCATE TABLE dbo.LoadEvidence_Promotion;

    UPDATE dbo.Inbox SET Status = 'Batched' WHERE BatchId IS NOT NULL AND Status <> 'Batched';
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ResetAll: empties everything, including the Inbox and the target. For a fresh test run.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ResetAll
AS
BEGIN
    SET NOCOUNT ON;

    EXEC dbo.usp_ResetStaging @AlsoResetTarget = 1;

    TRUNCATE TABLE dbo.Inbox;
    TRUNCATE TABLE dbo.InboxDuplicate;
    TRUNCATE TABLE dbo.TestManifest;
    TRUNCATE TABLE dbo.LoadRun;
    TRUNCATE TABLE dbo.LoadRunBatch;
END;
GO
