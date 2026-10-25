-- 060: Approach A driver, written like a SQL Agent job step: loop over batches, one transaction
-- per batch (inside usp_ShredBatch). A failing batch is rolled back, logged and skipped; the loop
-- continues. Safe to run more than once.
--
-- Run it as a job step or from any client:
--     EXEC SapStaging.dbo.usp_RunShredDriver;                 -- all batches not yet staged
--     EXEC SapStaging.dbo.usp_RunShredDriver @OnlyPending = 0; -- reload every batch not approved
USE SapStaging;
GO

CREATE OR ALTER PROCEDURE dbo.usp_RunShredDriver
    @OnlyPending bit = 1,
    @Note        nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RunId int;
    DECLARE @BatchId int;
    DECLARE @StartedAt datetime2(3);
    DECLARE @FailedCount int = 0;
    DECLARE @BatchCount int = 0;

    INSERT INTO dbo.LoadRun (Approach, DegreeOfParallelism, StartedAt, Note)
    VALUES ('A', 1, SYSUTCDATETIME(), @Note);
    SET @RunId = SCOPE_IDENTITY();

    DECLARE BatchCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT i.BatchId
        FROM dbo.Inbox i
        LEFT JOIN dbo.LoadEvidence_Batch b ON b.BatchId = i.BatchId
        WHERE i.BatchId IS NOT NULL
          AND (b.BatchId IS NULL OR (@OnlyPending = 0 AND b.Status = 'Staged'))
        ORDER BY i.BatchId;

    OPEN BatchCursor;
    FETCH NEXT FROM BatchCursor INTO @BatchId;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @StartedAt = SYSUTCDATETIME();
        SET @BatchCount += 1;

        BEGIN TRY
            EXEC dbo.usp_ShredBatch @BatchId = @BatchId;

            INSERT INTO dbo.LoadRunBatch (RunId, BatchId, StartedAt, EndedAt, IsSucceeded)
            VALUES (@RunId, @BatchId, @StartedAt, SYSUTCDATETIME(), 1);
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

            SET @FailedCount += 1;

            INSERT INTO dbo.LoadRunBatch (RunId, BatchId, StartedAt, EndedAt, IsSucceeded, ErrorMessage)
            VALUES (@RunId, @BatchId, @StartedAt, SYSUTCDATETIME(), 0,
                    CONCAT(N'Error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE()));
        END CATCH;

        FETCH NEXT FROM BatchCursor INTO @BatchId;
    END;

    CLOSE BatchCursor;
    DEALLOCATE BatchCursor;

    UPDATE dbo.LoadRun
    SET EndedAt = SYSUTCDATETIME(), BatchCount = @BatchCount, FailedBatchCount = @FailedCount
    WHERE RunId = @RunId;

    SELECT RunId, Approach, StartedAt, EndedAt, BatchCount, FailedBatchCount,
           DATEDIFF(millisecond, StartedAt, EndedAt) AS ElapsedMs
    FROM dbo.LoadRun
    WHERE RunId = @RunId;
END;
GO
