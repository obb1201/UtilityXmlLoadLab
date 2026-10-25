-- 080: Promotion gate - approve, reject, and promote batches into SapTarget. Safe to run more than once.
--
-- Rules
-- * The reference check (usp_CheckReferences) must be newer than the last staged or rejected batch.
-- * Auto-approve only when the batch is perfectly clean: FailedCount = 0 and no findings at all.
-- * Manual approve is allowed with failures or findings; the counts are saved in ApprovalNote.
-- * Promote in batch order: no earlier batch may still be Staged or Approved. This keeps
--   "latest version wins" correct and the target's foreign keys valid.
-- * Rows from failed messages and rows with an Error finding are never promoted (counted as Excluded).
-- * An older message never overwrites newer data already in the target (SourceCreatedAt guard).
USE SapStaging;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_AssertReferencesChecked: throws when the reference check is missing or out of date.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_AssertReferencesChecked
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LastChange datetime2(3) =
    (
        SELECT MAX(v.ChangedAt)
        FROM dbo.LoadEvidence_Batch b
        CROSS APPLY (VALUES (b.StagedAt), (b.RejectedAt)) v(ChangedAt)
        WHERE v.ChangedAt IS NOT NULL
    );

    IF EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch
               WHERE Status IN ('Staged', 'Approved')
                 AND (ReferencesCheckedAt IS NULL OR ReferencesCheckedAt < @LastChange))
    BEGIN
        THROW 50040, 'The reference check is missing or out of date. Run dbo.usp_CheckReferences first.', 1;
    END;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ApproveBatch
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ApproveBatch
    @BatchId     int,
    @ApprovedBy  nvarchar(100),
    @AutoApprove bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @ApprovedBy IS NULL OR LEN(@ApprovedBy) = 0
    BEGIN
        THROW 50041, 'ApprovedBy is required.', 1;
    END;

    BEGIN TRANSACTION;

    EXEC dbo.usp_AssertReferencesChecked;

    DECLARE @Status varchar(10), @Failed int, @Errors int, @Warnings int;

    SELECT @Status = Status, @Failed = FailedCount, @Errors = ErrorFindingCount, @Warnings = WarningFindingCount
    FROM dbo.LoadEvidence_Batch WITH (UPDLOCK, HOLDLOCK)
    WHERE BatchId = @BatchId;

    IF @Status IS NULL OR @Status <> 'Staged'
    BEGIN
        THROW 50042, 'Only a Staged batch can be approved.', 1;
    END;

    IF @AutoApprove = 1 AND (@Failed > 0 OR @Errors > 0 OR @Warnings > 0)
    BEGIN
        DECLARE @Text nvarchar(300) = CONCAT(N'Auto-approve refused: ', @Failed, N' failed messages, ',
                                             @Errors, N' error findings, ', @Warnings, N' warnings.');
        THROW 50043, @Text, 1;
    END;

    UPDATE dbo.LoadEvidence_Batch
    SET Status = 'Approved',
        ApprovedBy = @ApprovedBy,
        ApprovedAt = SYSUTCDATETIME(),
        ApprovalNote = CONCAT(CASE WHEN @AutoApprove = 1 THEN N'Auto-approved' ELSE N'Manually approved' END,
                              N' with ', @Failed, N' failed messages, ', @Errors, N' error findings, ',
                              @Warnings, N' warnings.')
    WHERE BatchId = @BatchId;

    COMMIT TRANSACTION;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_AutoApproveBatches: approves every Staged batch that is perfectly clean.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_AutoApproveBatches
    @ApprovedBy nvarchar(100) = N'auto-approve'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    EXEC dbo.usp_AssertReferencesChecked;

    UPDATE dbo.LoadEvidence_Batch
    SET Status = 'Approved',
        ApprovedBy = @ApprovedBy,
        ApprovedAt = SYSUTCDATETIME(),
        ApprovalNote = N'Auto-approved with 0 failed messages, 0 error findings, 0 warnings.'
    OUTPUT inserted.BatchId
    WHERE Status = 'Staged' AND FailedCount = 0 AND ErrorFindingCount = 0 AND WarningFindingCount = 0;

    COMMIT TRANSACTION;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_RejectBatch: a rejected batch is never promoted; its rows stop counting as references.
-- To retry later, fix the cause and reload it (usp_BeginBatch refuses only Approved/Promoted).
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_RejectBatch
    @BatchId    int,
    @RejectedBy nvarchar(100),
    @Reason     nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RejectedBy IS NULL OR LEN(@RejectedBy) = 0 OR @Reason IS NULL OR LEN(@Reason) = 0
    BEGIN
        THROW 50044, 'RejectedBy and Reason are required.', 1;
    END;

    UPDATE dbo.LoadEvidence_Batch
    SET Status = 'Rejected', RejectedBy = @RejectedBy, RejectedAt = SYSUTCDATETIME(), RejectReason = @Reason
    WHERE BatchId = @BatchId AND Status IN ('Staged', 'Approved');

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 50045, 'Only a Staged or Approved batch can be rejected.', 1;
    END;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_PromoteBatches: set-based MERGE of a range of Approved batches into SapTarget, in
-- dependency order: Customer -> Account -> Premise -> Installation -> Meter -> Contract ->
-- Agreement -> MoveInOut -> Payment. One transaction for the whole range.
--
-- For every staged row the outcome is recorded per batch and table in LoadEvidence_Promotion:
--   Promoted   - this row is the version now in the target,
--   Superseded - a newer version of the same entity exists (in the range or in the target),
--   Excluded   - the message failed validation, or the row has an Error finding.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_PromoteBatches
    @FirstBatchId int,
    @LastBatchId  int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Text nvarchar(400);

    BEGIN TRANSACTION;

    EXEC dbo.usp_AssertReferencesChecked;

    SELECT BatchId
    INTO #Range
    FROM dbo.LoadEvidence_Batch WITH (UPDLOCK, HOLDLOCK)
    WHERE BatchId BETWEEN @FirstBatchId AND @LastBatchId;

    IF NOT EXISTS (SELECT 1 FROM #Range)
    BEGIN
        THROW 50050, 'No staged batches in this range.', 1;
    END;

    IF EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch b JOIN #Range r ON r.BatchId = b.BatchId WHERE b.Status <> 'Approved')
    BEGIN
        THROW 50051, 'Every batch in the range must be Approved.', 1;
    END;

    IF EXISTS (SELECT 1 FROM dbo.Inbox i
               WHERE i.BatchId BETWEEN @FirstBatchId AND @LastBatchId
                 AND NOT EXISTS (SELECT 1 FROM #Range r WHERE r.BatchId = i.BatchId))
    BEGIN
        THROW 50052, 'Some batches in the range were never staged.', 1;
    END;

    IF EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch WHERE BatchId < @FirstBatchId AND Status IN ('Staged', 'Approved'))
       OR EXISTS (SELECT 1 FROM dbo.Inbox i
                  WHERE i.BatchId < @FirstBatchId
                    AND NOT EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch b WHERE b.BatchId = i.BatchId))
    BEGIN
        THROW 50053, 'Promote in order: an earlier batch is not yet Promoted or Rejected.', 1;
    END;

    SELECT @Text = CONCAT(N'Batch ', MIN(b.BatchId), N' needs batch ', MAX(b.DependsOnBatchId),
                          N'. Promote a range that includes it.')
    FROM dbo.LoadEvidence_Batch b
    JOIN #Range r ON r.BatchId = b.BatchId
    WHERE b.DependsOnBatchId > @LastBatchId
      AND NOT EXISTS (SELECT 1 FROM dbo.LoadEvidence_Batch d WHERE d.BatchId = b.DependsOnBatchId AND d.Status = 'Promoted')
    HAVING COUNT(*) > 0;

    IF @Text IS NOT NULL
    BEGIN
        THROW 50054, @Text, 1;
    END;

    -- Messages of the range, with status and creation time.
    SELECT e.MessageId, e.BatchId, e.ValidationStatus, h.CreatedAt
    INTO #Msg
    FROM dbo.LoadEvidence_Message e
    JOIN #Range r ON r.BatchId = e.BatchId
    LEFT JOIN stg.MessageHeader h ON h.MessageId = e.MessageId AND h.BatchId = e.BatchId
    WHERE e.ArrivalNo = 1;

    CREATE UNIQUE CLUSTERED INDEX CIX_Msg ON #Msg (MessageId);

    -- Error findings of the range: these rows are excluded.
    SELECT f.EntityType, f.EntityKey, f.MessageId
    INTO #Err
    FROM dbo.LoadEvidence_Finding f
    JOIN #Range r ON r.BatchId = f.BatchId
    WHERE f.Severity = 'Error';

    CREATE CLUSTERED INDEX CIX_Err ON #Err (EntityType, EntityKey, MessageId);

    CREATE TABLE #Outcome (BatchId int NOT NULL, TableName varchar(30) NOT NULL, Outcome char(1) NOT NULL);

    --------------------------------------------------------------------------------------------
    -- Customer
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Customer' AND e.EntityKey = s.CustomerId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Customer
    FROM stg.Customer s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, CustomerId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY CustomerId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Customer WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Customer t WHERE t.CustomerId = w.CustomerId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Customer AS t
    USING (SELECT * FROM #Customer WHERE Outcome = 'P') AS s
        ON t.CustomerId = s.CustomerId
    WHEN MATCHED THEN UPDATE SET
        Category = s.Category, FirstName = s.FirstName, LastName = s.LastName, OrgName = s.OrgName, Email = s.Email,
        Phone = s.Phone, ValidFrom = s.ValidFrom, SourceMessageId = s.MessageId, SourceBatchId = s.BatchId,
        SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (CustomerId, Category, FirstName, LastName, OrgName, Email, Phone, ValidFrom, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.CustomerId, s.Category, s.FirstName, s.LastName, s.OrgName, s.Email, s.Phone, s.ValidFrom, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Customer', Outcome FROM #Customer;

    --------------------------------------------------------------------------------------------
    -- CustomerAddress (child of Customer)
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Customer t WHERE t.CustomerId = s.CustomerId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #CustomerAddress
    FROM stg.CustomerAddress s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, CustomerId, AddressType, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY CustomerId, AddressType ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #CustomerAddress WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.CustomerAddress t
                      WHERE t.CustomerId = w.CustomerId AND t.AddressType = w.AddressType AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.CustomerAddress AS t
    USING (SELECT * FROM #CustomerAddress WHERE Outcome = 'P') AS s
        ON t.CustomerId = s.CustomerId AND t.AddressType = s.AddressType
    WHEN MATCHED THEN UPDATE SET
        Street = s.Street, HouseNo = s.HouseNo, PostalCode = s.PostalCode, City = s.City, Country = s.Country,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (CustomerId, AddressType, Street, HouseNo, PostalCode, City, Country, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.CustomerId, s.AddressType, s.Street, s.HouseNo, s.PostalCode, s.City, s.Country, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'CustomerAddress', Outcome FROM #CustomerAddress;

    --------------------------------------------------------------------------------------------
    -- Account
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Account' AND e.EntityKey = s.AccountId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Account
    FROM stg.Account s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, AccountId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY AccountId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Account WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Account t WHERE t.AccountId = w.AccountId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Account AS t
    USING (SELECT * FROM #Account WHERE Outcome = 'P') AS s
        ON t.AccountId = s.AccountId
    WHEN MATCHED THEN UPDATE SET
        CustomerId = s.CustomerId, AccountClass = s.AccountClass, BillingCycle = s.BillingCycle,
        PaymentMethod = s.PaymentMethod, CreditLimit = s.CreditLimit, OpenedOn = s.OpenedOn,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (AccountId, CustomerId, AccountClass, BillingCycle, PaymentMethod, CreditLimit, OpenedOn, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.AccountId, s.CustomerId, s.AccountClass, s.BillingCycle, s.PaymentMethod, s.CreditLimit, s.OpenedOn, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Account', Outcome FROM #Account;

    --------------------------------------------------------------------------------------------
    -- Premise
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Premise' AND e.EntityKey = s.PremiseId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Premise
    FROM stg.Premise s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, PremiseId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY PremiseId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Premise WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Premise t WHERE t.PremiseId = w.PremiseId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Premise AS t
    USING (SELECT * FROM #Premise WHERE Outcome = 'P') AS s
        ON t.PremiseId = s.PremiseId
    WHEN MATCHED THEN UPDATE SET
        ConnectionObjectId = s.ConnectionObjectId, PremiseType = s.PremiseType, Street = s.Street, HouseNo = s.HouseNo,
        Unit = s.Unit, PostalCode = s.PostalCode, City = s.City, Country = s.Country,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (PremiseId, ConnectionObjectId, PremiseType, Street, HouseNo, Unit, PostalCode, City, Country, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.PremiseId, s.ConnectionObjectId, s.PremiseType, s.Street, s.HouseNo, s.Unit, s.PostalCode, s.City, s.Country, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Premise', Outcome FROM #Premise;

    --------------------------------------------------------------------------------------------
    -- Installation
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Installation' AND e.EntityKey = s.InstallationId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Installation
    FROM stg.Installation s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, InstallationId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY InstallationId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Installation WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Installation t WHERE t.InstallationId = w.InstallationId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Installation AS t
    USING (SELECT * FROM #Installation WHERE Outcome = 'P') AS s
        ON t.InstallationId = s.InstallationId
    WHEN MATCHED THEN UPDATE SET
        PremiseId = s.PremiseId, Division = s.Division, RateCategory = s.RateCategory, ValidFrom = s.ValidFrom,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (InstallationId, PremiseId, Division, RateCategory, ValidFrom, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.InstallationId, s.PremiseId, s.Division, s.RateCategory, s.ValidFrom, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Installation', Outcome FROM #Installation;

    --------------------------------------------------------------------------------------------
    -- Meter: the device (dbo.Meter), its installation periods (dbo.MeterInstall), and registers.
    -- Outcomes for stg.Meter rows are counted on the installation period.
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e
                                  WHERE e.EntityType = 'Meter' AND e.MessageId = s.MessageId
                                    AND e.EntityKey = CONCAT(s.MeterId, '|', s.InstallationId, '|', CONVERT(char(10), s.InstallDate, 23)))
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Meter
    FROM stg.Meter s JOIN #Msg m ON m.MessageId = s.MessageId;

    -- Device: latest usable version per MeterId.
    MERGE SapTarget.dbo.Meter AS t
    USING
    (
        SELECT x.*
        FROM
        (
            SELECT d.*, ROW_NUMBER() OVER (PARTITION BY d.MeterId ORDER BY d.CreatedAt DESC, d.BatchId DESC, d.MessageId DESC) AS RowNo
            FROM #Meter d WHERE d.Outcome <> 'E'
        ) x
        WHERE x.RowNo = 1
          AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Meter t2 WHERE t2.MeterId = x.MeterId AND t2.SourceCreatedAt > x.CreatedAt)
    ) AS s
        ON t.MeterId = s.MeterId
    WHEN MATCHED THEN UPDATE SET
        SerialNumber = s.SerialNumber, MeterType = s.MeterType, Manufacturer = s.Manufacturer,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (MeterId, SerialNumber, MeterType, Manufacturer, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.MeterId, s.SerialNumber, s.MeterType, s.Manufacturer, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    WITH Winner AS
    (
        SELECT Outcome, MeterId, InstallationId, InstallDate, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY MeterId, InstallationId, InstallDate ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Meter WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.MeterInstall t
                      WHERE t.MeterId = w.MeterId AND t.InstallationId = w.InstallationId AND t.InstallDate = w.InstallDate
                        AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.MeterInstall AS t
    USING (SELECT * FROM #Meter WHERE Outcome = 'P') AS s
        ON t.MeterId = s.MeterId AND t.InstallationId = s.InstallationId AND t.InstallDate = s.InstallDate
    WHEN MATCHED THEN UPDATE SET
        RemoveDate = s.RemoveDate, SourceMessageId = s.MessageId, SourceBatchId = s.BatchId,
        SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (MeterId, InstallationId, InstallDate, RemoveDate, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.MeterId, s.InstallationId, s.InstallDate, s.RemoveDate, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Meter', Outcome FROM #Meter;

    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Meter t WHERE t.MeterId = s.MeterId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #MeterRegister
    FROM stg.MeterRegister s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, MeterId, RegisterNo, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY MeterId, RegisterNo ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #MeterRegister WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.MeterRegister t
                      WHERE t.MeterId = w.MeterId AND t.RegisterNo = w.RegisterNo AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.MeterRegister AS t
    USING (SELECT * FROM #MeterRegister WHERE Outcome = 'P') AS s
        ON t.MeterId = s.MeterId AND t.RegisterNo = s.RegisterNo
    WHEN MATCHED THEN UPDATE SET
        RegisterType = s.RegisterType, UnitOfMeasure = s.UnitOfMeasure, Decimals = s.Decimals,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (MeterId, RegisterNo, RegisterType, UnitOfMeasure, Decimals, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.MeterId, s.RegisterNo, s.RegisterType, s.UnitOfMeasure, s.Decimals, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'MeterRegister', Outcome FROM #MeterRegister;

    --------------------------------------------------------------------------------------------
    -- Contract
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Contract' AND e.EntityKey = s.ContractId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Contract
    FROM stg.Contract s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, ContractId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY ContractId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Contract WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Contract t WHERE t.ContractId = w.ContractId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Contract AS t
    USING (SELECT * FROM #Contract WHERE Outcome = 'P') AS s
        ON t.ContractId = s.ContractId
    WHEN MATCHED THEN UPDATE SET
        AccountId = s.AccountId, InstallationId = s.InstallationId, StartDate = s.StartDate, EndDate = s.EndDate,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (ContractId, AccountId, InstallationId, StartDate, EndDate, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.ContractId, s.AccountId, s.InstallationId, s.StartDate, s.EndDate, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Contract', Outcome FROM #Contract;

    --------------------------------------------------------------------------------------------
    -- Agreement
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Agreement' AND e.EntityKey = s.AgreementId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Agreement
    FROM stg.Agreement s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, AgreementId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY AgreementId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Agreement WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Agreement t WHERE t.AgreementId = w.AgreementId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Agreement AS t
    USING (SELECT * FROM #Agreement WHERE Outcome = 'P') AS s
        ON t.AgreementId = s.AgreementId
    WHEN MATCHED THEN UPDATE SET
        ContractId = s.ContractId, AgreementType = s.AgreementType, PriceLevel = s.PriceLevel,
        DiscountPct = s.DiscountPct, ValidFrom = s.ValidFrom, ValidTo = s.ValidTo,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (AgreementId, ContractId, AgreementType, PriceLevel, DiscountPct, ValidFrom, ValidTo, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.AgreementId, s.ContractId, s.AgreementType, s.PriceLevel, s.DiscountPct, s.ValidFrom, s.ValidTo, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Agreement', Outcome FROM #Agreement;

    --------------------------------------------------------------------------------------------
    -- MoveInOut
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'MoveInOut' AND e.EntityKey = s.EventId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #MoveInOut
    FROM stg.MoveInOut s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, EventId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY EventId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #MoveInOut WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.MoveInOut t WHERE t.EventId = w.EventId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.MoveInOut AS t
    USING (SELECT * FROM #MoveInOut WHERE Outcome = 'P') AS s
        ON t.EventId = s.EventId
    WHEN MATCHED THEN UPDATE SET
        EventType = s.EventType, CustomerId = s.CustomerId, AccountId = s.AccountId, PremiseId = s.PremiseId,
        EventDate = s.EventDate, SourceMessageId = s.MessageId, SourceBatchId = s.BatchId,
        SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (EventId, EventType, CustomerId, AccountId, PremiseId, EventDate, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.EventId, s.EventType, s.CustomerId, s.AccountId, s.PremiseId, s.EventDate, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'MoveInOut', Outcome FROM #MoveInOut;

    --------------------------------------------------------------------------------------------
    -- Payment
    --------------------------------------------------------------------------------------------
    SELECT s.*, m.CreatedAt,
           CAST(CASE WHEN m.ValidationStatus <> 'Passed'
                       OR EXISTS (SELECT 1 FROM #Err e WHERE e.EntityType = 'Payment' AND e.EntityKey = s.PaymentId
                                                         AND e.MessageId = s.MessageId)
                     THEN 'E' ELSE 'S' END AS char(1)) AS Outcome
    INTO #Payment
    FROM stg.Payment s JOIN #Msg m ON m.MessageId = s.MessageId;

    WITH Winner AS
    (
        SELECT Outcome, PaymentId, CreatedAt,
               ROW_NUMBER() OVER (PARTITION BY PaymentId ORDER BY CreatedAt DESC, BatchId DESC, MessageId DESC) AS RowNo
        FROM #Payment WHERE Outcome <> 'E'
    )
    UPDATE w SET Outcome = 'P'
    FROM Winner w
    WHERE w.RowNo = 1
      AND NOT EXISTS (SELECT 1 FROM SapTarget.dbo.Payment t WHERE t.PaymentId = w.PaymentId AND t.SourceCreatedAt > w.CreatedAt);

    MERGE SapTarget.dbo.Payment AS t
    USING (SELECT * FROM #Payment WHERE Outcome = 'P') AS s
        ON t.PaymentId = s.PaymentId
    WHEN MATCHED THEN UPDATE SET
        AccountId = s.AccountId, Amount = s.Amount, Currency = s.Currency, PaymentDate = s.PaymentDate,
        PaymentMethod = s.PaymentMethod, Reference = s.Reference,
        SourceMessageId = s.MessageId, SourceBatchId = s.BatchId, SourceCreatedAt = s.CreatedAt, PromotedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN INSERT
        (PaymentId, AccountId, Amount, Currency, PaymentDate, PaymentMethod, Reference, SourceMessageId, SourceBatchId, SourceCreatedAt, PromotedAt)
        VALUES (s.PaymentId, s.AccountId, s.Amount, s.Currency, s.PaymentDate, s.PaymentMethod, s.Reference, s.MessageId, s.BatchId, s.CreatedAt, @Now);

    INSERT INTO #Outcome SELECT BatchId, 'Payment', Outcome FROM #Payment;

    --------------------------------------------------------------------------------------------
    -- Promotion evidence, reconciliation, batch status.
    --------------------------------------------------------------------------------------------
    DELETE p FROM dbo.LoadEvidence_Promotion p JOIN #Range r ON r.BatchId = p.BatchId;

    INSERT INTO dbo.LoadEvidence_Promotion (BatchId, TableName, StagedRows, PromotedRows, SupersededRows, ExcludedRows, PromotedAt)
    SELECT BatchId, TableName, COUNT(*),
           SUM(CASE WHEN Outcome = 'P' THEN 1 ELSE 0 END),
           SUM(CASE WHEN Outcome = 'S' THEN 1 ELSE 0 END),
           SUM(CASE WHEN Outcome = 'E' THEN 1 ELSE 0 END),
           @Now
    FROM #Outcome
    GROUP BY BatchId, TableName;

    -- Every staged row (except message headers, which are metadata) must have an outcome.
    SELECT @Text = CONCAT(N'Reconciliation failed for batch ', MIN(b.BatchId),
                          N': staged rows do not match promotion outcomes.')
    FROM dbo.LoadEvidence_Batch b
    JOIN #Range r ON r.BatchId = b.BatchId
    WHERE b.StagedRowTotal <>
          (SELECT COUNT(*) FROM stg.MessageHeader h WHERE h.BatchId = b.BatchId)
          + ISNULL((SELECT SUM(p.StagedRows) FROM dbo.LoadEvidence_Promotion p WHERE p.BatchId = b.BatchId), 0)
    HAVING COUNT(*) > 0;

    IF @Text IS NOT NULL
    BEGIN
        THROW 50055, @Text, 1;
    END;

    UPDATE b
    SET Status = 'Promoted',
        PromotedAt = @Now,
        PromotedRowTotal = ISNULL((SELECT SUM(p.PromotedRows) FROM dbo.LoadEvidence_Promotion p WHERE p.BatchId = b.BatchId), 0)
    FROM dbo.LoadEvidence_Batch b
    JOIN #Range r ON r.BatchId = b.BatchId;

    COMMIT TRANSACTION;

    SELECT TableName, SUM(StagedRows) AS StagedRows, SUM(PromotedRows) AS PromotedRows,
           SUM(SupersededRows) AS SupersededRows, SUM(ExcludedRows) AS ExcludedRows
    FROM dbo.LoadEvidence_Promotion p
    JOIN #Range r ON r.BatchId = p.BatchId
    GROUP BY TableName
    ORDER BY TableName;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_PromoteBatch: promote a single Approved batch.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_PromoteBatch
    @BatchId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.usp_PromoteBatches @FirstBatchId = @BatchId, @LastBatchId = @BatchId;
END;
GO
