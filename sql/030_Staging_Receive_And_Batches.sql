-- 030: Receiving messages, creating batches, and the shared field-validation function.
-- Safe to run more than once.
USE SapStaging;
GO

------------------------------------------------------------------------------------------------
-- dbo.fn_FieldProblem: returns NULL when a raw XML value is valid, otherwise a short problem text.
-- Used by approach A (T-SQL). Approach B applies the same rules in FieldValidator.cs.
-- Types: T = text, D = date (yyyy-MM-dd), DT = date-time (yyyy-MM-ddTHH:mm:ss),
--        N = decimal(18,2), P = decimal(5,2), I = int.
-- Formats are strict on purpose, so both approaches accept exactly the same values.
------------------------------------------------------------------------------------------------
CREATE OR ALTER FUNCTION dbo.fn_FieldProblem
(
    @Value      nvarchar(400),
    @FieldName  varchar(40),
    @Type       varchar(2),
    @MaxLength  int,
    @IsRequired bit
)
RETURNS nvarchar(300)
AS
BEGIN
    RETURN CASE
        WHEN @Value IS NULL
            THEN CASE WHEN @IsRequired = 1 THEN CONCAT(@FieldName, N' is missing') END
        WHEN @Type = 'T' AND DATALENGTH(@Value) / 2 > @MaxLength
            THEN CONCAT(@FieldName, N' is longer than ', @MaxLength, N' characters')
        WHEN @Type = 'D'
             AND (@Value NOT LIKE '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
                  OR TRY_CONVERT(date, @Value, 23) IS NULL)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' is not a valid date')
        WHEN @Type = 'DT'
             AND (@Value NOT LIKE '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
                  OR TRY_CONVERT(datetime2(0), @Value, 126) IS NULL)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' is not a valid date-time')
        WHEN @Type = 'N'
             AND (@Value LIKE '%[^0-9.-]%' OR TRY_CONVERT(decimal(18,2), @Value) IS NULL)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' is not a valid decimal')
        WHEN @Type = 'N' AND TRY_CONVERT(decimal(38,10), @Value) <> TRY_CONVERT(decimal(18,2), @Value)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' has more than 2 decimal places')
        WHEN @Type = 'P'
             AND (@Value LIKE '%[^0-9.-]%' OR TRY_CONVERT(decimal(5,2), @Value) IS NULL)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' is not a valid decimal')
        WHEN @Type = 'P' AND TRY_CONVERT(decimal(38,10), @Value) <> TRY_CONVERT(decimal(5,2), @Value)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' has more than 2 decimal places')
        WHEN @Type = 'I'
             AND (@Value LIKE '%[^0-9-]%' OR TRY_CONVERT(int, @Value) IS NULL)
            THEN CONCAT(@FieldName, N' ''', LEFT(@Value, 40), N''' is not a valid integer')
    END;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ReceiveMessage: used by the CoreWCF receiver. No parsing.
-- A duplicate MessageId is recorded in InboxDuplicate and still reported as success.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ReceiveMessage
    @MessageId varchar(50),
    @RawXml    nvarchar(max),
    @Outcome   varchar(20) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    INSERT INTO dbo.Inbox (MessageId, RawXml)
    SELECT @MessageId, @RawXml
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Inbox WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = @MessageId);

    IF @@ROWCOUNT = 1
    BEGIN
        SET @Outcome = 'Accepted';
    END
    ELSE
    BEGIN
        INSERT INTO dbo.InboxDuplicate (MessageId, RawXml) VALUES (@MessageId, @RawXml);
        SET @Outcome = 'Duplicate';
    END;

    COMMIT TRANSACTION;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ReceiveLanding: the fast "direct insert" path used by the generator.
-- The caller bulk-copies a chunk into #InboxLanding (LandingSeq, MessageId, RawXml) on the same
-- connection, then calls this procedure. Same rules as usp_ReceiveMessage.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ReceiveLanding
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    SELECT l.LandingSeq,
           CAST(CASE
                    WHEN ROW_NUMBER() OVER (PARTITION BY l.MessageId ORDER BY l.LandingSeq) = 1
                         AND NOT EXISTS (SELECT 1 FROM dbo.Inbox i WITH (UPDLOCK, HOLDLOCK) WHERE i.MessageId = l.MessageId)
                        THEN 1
                    ELSE 0
                END AS bit) AS IsNew
    INTO #Classified
    FROM #InboxLanding l;

    -- ORDER BY keeps ArrivalSeq in landing (arrival) order.
    INSERT INTO dbo.Inbox (MessageId, RawXml)
    SELECT l.MessageId, l.RawXml
    FROM #InboxLanding l
    JOIN #Classified c ON c.LandingSeq = l.LandingSeq
    WHERE c.IsNew = 1
    ORDER BY l.LandingSeq;

    INSERT INTO dbo.InboxDuplicate (MessageId, RawXml)
    SELECT l.MessageId, l.RawXml
    FROM #InboxLanding l
    JOIN #Classified c ON c.LandingSeq = l.LandingSeq
    WHERE c.IsNew = 0
    ORDER BY l.LandingSeq;

    COMMIT TRANSACTION;

    SELECT SUM(CASE WHEN IsNew = 1 THEN 1 ELSE 0 END) AS AcceptedCount,
           SUM(CASE WHEN IsNew = 0 THEN 1 ELSE 0 END) AS DuplicateCount
    FROM #Classified;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_CreateBatches: puts every unbatched Inbox message into a batch, in arrival order.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_CreateBatches
    @BatchSize int = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1
    BEGIN
        THROW 50001, 'BatchSize must be 1 or more.', 1;
    END;

    BEGIN TRANSACTION;

    DECLARE @LastBatchId int = ISNULL((SELECT MAX(BatchId) FROM dbo.Inbox WITH (UPDLOCK, HOLDLOCK)), 0);

    WITH Unbatched AS
    (
        SELECT BatchId, Status, ROW_NUMBER() OVER (ORDER BY ArrivalSeq) AS RowNo
        FROM dbo.Inbox
        WHERE BatchId IS NULL
    )
    UPDATE Unbatched
    SET BatchId = @LastBatchId + (RowNo - 1) / @BatchSize + 1,
        Status = 'Batched';

    COMMIT TRANSACTION;

    SELECT BatchId, COUNT(*) AS MessageCount
    FROM dbo.Inbox
    WHERE BatchId > @LastBatchId
    GROUP BY BatchId
    ORDER BY BatchId;
END;
GO
