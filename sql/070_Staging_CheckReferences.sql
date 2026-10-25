-- 070: Cross-entity checks before promotion. Safe to run more than once.
--
-- dbo.usp_CheckReferences recomputes the findings for every batch that is Staged or Approved.
-- It looks across ALL those batches plus SapTarget, because messages arrive in any order and a
-- reference may point to an entity in a later message (even a later batch).
--
-- Errors (the row cannot be promoted; it would break a target foreign key):
--   OrphanReference   - a referenced entity is not in staging (passed messages) or in the target.
--                       Checked in dependency order, so if a contract is an orphan, its agreements
--                       become orphans too.
-- Warnings (business rules; block auto-approval only):
--   MeterInMultipleInstallations - the same meter is installed in two installations at once.
--   OverlappingContracts         - two contracts for the same installation overlap in time.
--
-- It also sets LoadEvidence_Batch.DependsOnBatchId: the highest later batch that this batch needs
-- (because a referenced entity only exists there). Promotion uses it to keep target keys valid.
USE SapStaging;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CheckReferences
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    SELECT BatchId
    INTO #Scope
    FROM dbo.LoadEvidence_Batch WITH (UPDLOCK, HOLDLOCK)
    WHERE Status IN ('Staged', 'Approved');

    DELETE f
    FROM dbo.LoadEvidence_Finding f
    JOIN #Scope s ON s.BatchId = f.BatchId;

    -- Passed messages in scope, with their creation time (used to pick the latest version).
    SELECT e.MessageId, e.BatchId, h.CreatedAt
    INTO #Ok
    FROM dbo.LoadEvidence_Message e
    JOIN #Scope s ON s.BatchId = e.BatchId
    JOIN stg.MessageHeader h ON h.MessageId = e.MessageId AND h.BatchId = e.BatchId
    WHERE e.ArrivalNo = 1 AND e.ValidationStatus = 'Passed';

    CREATE UNIQUE CLUSTERED INDEX CIX_Ok ON #Ok (MessageId);

    CREATE TABLE #Finding
    (
        BatchId    int           NOT NULL,
        MessageId  varchar(50)   NOT NULL,
        Severity   varchar(10)   NOT NULL,
        CheckName  varchar(40)   NOT NULL,
        EntityType varchar(20)   NOT NULL,
        EntityKey  varchar(80)   NOT NULL,
        Detail     nvarchar(400) NOT NULL
    );

    --------------------------------------------------------------------------------------------
    -- Every reference from a staged row to another entity.
    --------------------------------------------------------------------------------------------
    CREATE TABLE #Ref
    (
        BatchId    int         NOT NULL,
        MessageId  varchar(50) NOT NULL,
        EntityType varchar(20) NOT NULL,
        EntityKey  varchar(80) NOT NULL,
        RefType    varchar(20) NOT NULL,
        RefId      varchar(20) NOT NULL
    );

    INSERT INTO #Ref (BatchId, MessageId, EntityType, EntityKey, RefType, RefId)
    SELECT s.BatchId, s.MessageId, 'Account', s.AccountId, 'Customer', s.CustomerId
    FROM stg.Account s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Installation', s.InstallationId, 'Premise', s.PremiseId
    FROM stg.Installation s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Meter', CONCAT(s.MeterId, '|', s.InstallationId, '|', CONVERT(char(10), s.InstallDate, 23)),
           'Installation', s.InstallationId
    FROM stg.Meter s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Contract', s.ContractId, 'Account', s.AccountId
    FROM stg.Contract s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Contract', s.ContractId, 'Installation', s.InstallationId
    FROM stg.Contract s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Agreement', s.AgreementId, 'Contract', s.ContractId
    FROM stg.Agreement s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'MoveInOut', s.EventId, 'Customer', s.CustomerId
    FROM stg.MoveInOut s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'MoveInOut', s.EventId, 'Account', s.AccountId
    FROM stg.MoveInOut s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'MoveInOut', s.EventId, 'Premise', s.PremiseId
    FROM stg.MoveInOut s JOIN #Ok o ON o.MessageId = s.MessageId
    UNION ALL
    SELECT s.BatchId, s.MessageId, 'Payment', s.PaymentId, 'Account', s.AccountId
    FROM stg.Payment s JOIN #Ok o ON o.MessageId = s.MessageId;

    CREATE CLUSTERED INDEX CIX_Ref ON #Ref (RefType, RefId);

    -- Entities that a reference can resolve to. InTarget = 1: already in SapTarget.
    -- Otherwise MinBatchId = the earliest staged batch holding a usable (not excluded) version.
    CREATE TABLE #Pool
    (
        EntityType varchar(20) NOT NULL,
        EntityId   varchar(20) NOT NULL,
        InTarget   bit         NOT NULL,
        MinBatchId int         NULL,
        PRIMARY KEY (EntityType, EntityId)
    );

    -- Rows already excluded by an Error finding (filled step by step, in dependency order).
    CREATE TABLE #Excluded
    (
        EntityType varchar(20) NOT NULL,
        EntityKey  varchar(80) NOT NULL,
        MessageId  varchar(50) NOT NULL
    );

    DECLARE @Level int = 1;

    WHILE @Level <= 3
    BEGIN
        -- Level 1: Customer, Premise (they reference nothing).
        -- Level 2: Account, Installation. Level 3: Contract.
        IF @Level = 1
        BEGIN
            INSERT INTO #Pool (EntityType, EntityId, InTarget, MinBatchId)
            SELECT 'Customer', x.CustomerId, MAX(x.InTarget), MIN(x.BatchId)
            FROM
            (
                SELECT t.CustomerId, CAST(1 AS int) AS InTarget, CAST(NULL AS int) AS BatchId
                FROM SapTarget.dbo.Customer t
                UNION ALL
                SELECT s.CustomerId, 0, s.BatchId FROM stg.Customer s JOIN #Ok o ON o.MessageId = s.MessageId
            ) x
            GROUP BY x.CustomerId;

            INSERT INTO #Pool (EntityType, EntityId, InTarget, MinBatchId)
            SELECT 'Premise', x.PremiseId, MAX(x.InTarget), MIN(x.BatchId)
            FROM
            (
                SELECT t.PremiseId, CAST(1 AS int) AS InTarget, CAST(NULL AS int) AS BatchId
                FROM SapTarget.dbo.Premise t
                UNION ALL
                SELECT s.PremiseId, 0, s.BatchId FROM stg.Premise s JOIN #Ok o ON o.MessageId = s.MessageId
            ) x
            GROUP BY x.PremiseId;
        END
        ELSE IF @Level = 2
        BEGIN
            INSERT INTO #Pool (EntityType, EntityId, InTarget, MinBatchId)
            SELECT 'Account', x.AccountId, MAX(x.InTarget), MIN(x.BatchId)
            FROM
            (
                SELECT t.AccountId, CAST(1 AS int) AS InTarget, CAST(NULL AS int) AS BatchId
                FROM SapTarget.dbo.Account t
                UNION ALL
                SELECT s.AccountId, 0, s.BatchId
                FROM stg.Account s JOIN #Ok o ON o.MessageId = s.MessageId
                WHERE NOT EXISTS (SELECT 1 FROM #Excluded e
                                  WHERE e.EntityType = 'Account' AND e.EntityKey = s.AccountId AND e.MessageId = s.MessageId)
            ) x
            GROUP BY x.AccountId;

            INSERT INTO #Pool (EntityType, EntityId, InTarget, MinBatchId)
            SELECT 'Installation', x.InstallationId, MAX(x.InTarget), MIN(x.BatchId)
            FROM
            (
                SELECT t.InstallationId, CAST(1 AS int) AS InTarget, CAST(NULL AS int) AS BatchId
                FROM SapTarget.dbo.Installation t
                UNION ALL
                SELECT s.InstallationId, 0, s.BatchId
                FROM stg.Installation s JOIN #Ok o ON o.MessageId = s.MessageId
                WHERE NOT EXISTS (SELECT 1 FROM #Excluded e
                                  WHERE e.EntityType = 'Installation' AND e.EntityKey = s.InstallationId AND e.MessageId = s.MessageId)
            ) x
            GROUP BY x.InstallationId;
        END
        ELSE
        BEGIN
            INSERT INTO #Pool (EntityType, EntityId, InTarget, MinBatchId)
            SELECT 'Contract', x.ContractId, MAX(x.InTarget), MIN(x.BatchId)
            FROM
            (
                SELECT t.ContractId, CAST(1 AS int) AS InTarget, CAST(NULL AS int) AS BatchId
                FROM SapTarget.dbo.Contract t
                UNION ALL
                SELECT s.ContractId, 0, s.BatchId
                FROM stg.Contract s JOIN #Ok o ON o.MessageId = s.MessageId
                WHERE NOT EXISTS (SELECT 1 FROM #Excluded e
                                  WHERE e.EntityType = 'Contract' AND e.EntityKey = s.ContractId AND e.MessageId = s.MessageId)
            ) x
            GROUP BY x.ContractId;
        END;

        -- Orphans: references of this level that resolve to nothing.
        INSERT INTO #Finding (BatchId, MessageId, Severity, CheckName, EntityType, EntityKey, Detail)
        SELECT r.BatchId, r.MessageId, 'Error', 'OrphanReference', r.EntityType, r.EntityKey,
               CONCAT(r.RefType, N' ', r.RefId, N' is not in staging (passed messages) or in the target.')
        FROM #Ref r
        WHERE r.RefType IN (CASE @Level WHEN 1 THEN 'Customer' WHEN 2 THEN 'Account' ELSE 'Contract' END,
                            CASE @Level WHEN 1 THEN 'Premise' WHEN 2 THEN 'Installation' ELSE 'Contract' END)
          AND NOT EXISTS (SELECT 1 FROM #Pool p WHERE p.EntityType = r.RefType AND p.EntityId = r.RefId);

        INSERT INTO #Excluded (EntityType, EntityKey, MessageId)
        SELECT DISTINCT f.EntityType, f.EntityKey, f.MessageId
        FROM #Finding f
        WHERE f.Severity = 'Error'
          AND NOT EXISTS (SELECT 1 FROM #Excluded e
                          WHERE e.EntityType = f.EntityType AND e.EntityKey = f.EntityKey AND e.MessageId = f.MessageId);

        SET @Level += 1;
    END;

    -- Batch dependencies: a reference that resolves only to a LATER batch (not the target).
    SELECT r.BatchId, MAX(p.MinBatchId) AS DependsOnBatchId
    INTO #Dep
    FROM #Ref r
    JOIN #Pool p ON p.EntityType = r.RefType AND p.EntityId = r.RefId
    WHERE p.InTarget = 0 AND p.MinBatchId > r.BatchId
      AND NOT EXISTS (SELECT 1 FROM #Excluded e
                      WHERE e.EntityType = r.EntityType AND e.EntityKey = r.EntityKey AND e.MessageId = r.MessageId)
    GROUP BY r.BatchId;

    --------------------------------------------------------------------------------------------
    -- Meter installed in more than one installation at the same time (latest versions only).
    --------------------------------------------------------------------------------------------
    SELECT x.MeterId, x.InstallationId, x.InstallDate, x.RemoveDate, x.BatchId, x.MessageId
    INTO #MeterPeriod
    FROM
    (
        SELECT v.*, ROW_NUMBER() OVER (PARTITION BY v.MeterId, v.InstallationId, v.InstallDate
                                       ORDER BY v.CreatedAt DESC, v.BatchId DESC) AS RowNo
        FROM
        (
            SELECT t.MeterId, t.InstallationId, t.InstallDate, t.RemoveDate, t.SourceCreatedAt AS CreatedAt,
                   CAST(NULL AS int) AS BatchId, CAST(NULL AS varchar(50)) AS MessageId
            FROM SapTarget.dbo.MeterInstall t
            UNION ALL
            SELECT s.MeterId, s.InstallationId, s.InstallDate, s.RemoveDate, o.CreatedAt, s.BatchId, s.MessageId
            FROM stg.Meter s JOIN #Ok o ON o.MessageId = s.MessageId
            WHERE NOT EXISTS (SELECT 1 FROM #Excluded e
                              WHERE e.EntityType = 'Meter' AND e.MessageId = s.MessageId
                                AND e.EntityKey = CONCAT(s.MeterId, '|', s.InstallationId, '|', CONVERT(char(10), s.InstallDate, 23)))
        ) v
    ) x
    WHERE x.RowNo = 1;

    CREATE CLUSTERED INDEX CIX_MeterPeriod ON #MeterPeriod (MeterId);

    INSERT INTO #Finding (BatchId, MessageId, Severity, CheckName, EntityType, EntityKey, Detail)
    SELECT a.BatchId, a.MessageId, 'Warning', 'MeterInMultipleInstallations', 'Meter',
           CONCAT(a.MeterId, '|', a.InstallationId, '|', CONVERT(char(10), a.InstallDate, 23)),
           CONCAT(N'Meter ', a.MeterId, N' in installation ', a.InstallationId, N' from ', CONVERT(char(10), a.InstallDate, 23),
                  N' overlaps installation ', b.InstallationId, N' from ', CONVERT(char(10), b.InstallDate, 23),
                  N' to ', ISNULL(CONVERT(char(10), b.RemoveDate, 23), N'open'), N'.')
    FROM #MeterPeriod a
    JOIN #MeterPeriod b
        ON b.MeterId = a.MeterId
       AND b.InstallationId <> a.InstallationId
       AND a.InstallDate < ISNULL(b.RemoveDate, '9999-12-31')
       AND b.InstallDate < ISNULL(a.RemoveDate, '9999-12-31')
    WHERE a.BatchId IS NOT NULL;

    --------------------------------------------------------------------------------------------
    -- Overlapping contracts for the same installation (latest version of each contract).
    --------------------------------------------------------------------------------------------
    SELECT x.ContractId, x.InstallationId, x.StartDate, x.EndDate, x.BatchId, x.MessageId
    INTO #ContractPeriod
    FROM
    (
        SELECT v.*, ROW_NUMBER() OVER (PARTITION BY v.ContractId ORDER BY v.CreatedAt DESC, v.BatchId DESC) AS RowNo
        FROM
        (
            SELECT t.ContractId, t.InstallationId, t.StartDate, t.EndDate, t.SourceCreatedAt AS CreatedAt,
                   CAST(NULL AS int) AS BatchId, CAST(NULL AS varchar(50)) AS MessageId
            FROM SapTarget.dbo.Contract t
            UNION ALL
            SELECT s.ContractId, s.InstallationId, s.StartDate, s.EndDate, o.CreatedAt, s.BatchId, s.MessageId
            FROM stg.Contract s JOIN #Ok o ON o.MessageId = s.MessageId
            WHERE NOT EXISTS (SELECT 1 FROM #Excluded e
                              WHERE e.EntityType = 'Contract' AND e.EntityKey = s.ContractId AND e.MessageId = s.MessageId)
        ) v
    ) x
    WHERE x.RowNo = 1;

    CREATE CLUSTERED INDEX CIX_ContractPeriod ON #ContractPeriod (InstallationId);

    INSERT INTO #Finding (BatchId, MessageId, Severity, CheckName, EntityType, EntityKey, Detail)
    SELECT a.BatchId, a.MessageId, 'Warning', 'OverlappingContracts', 'Contract', a.ContractId,
           CONCAT(N'Contract ', a.ContractId, N' (', CONVERT(char(10), a.StartDate, 23), N' to ',
                  ISNULL(CONVERT(char(10), a.EndDate, 23), N'open'), N') overlaps contract ', b.ContractId, N' (',
                  CONVERT(char(10), b.StartDate, 23), N' to ', ISNULL(CONVERT(char(10), b.EndDate, 23), N'open'),
                  N') on installation ', a.InstallationId, N'.')
    FROM #ContractPeriod a
    JOIN #ContractPeriod b
        ON b.InstallationId = a.InstallationId
       AND b.ContractId <> a.ContractId
       AND a.StartDate <= ISNULL(b.EndDate, '9999-12-31')
       AND b.StartDate <= ISNULL(a.EndDate, '9999-12-31')
    WHERE a.BatchId IS NOT NULL;

    --------------------------------------------------------------------------------------------
    -- Save findings and per-batch summary.
    --------------------------------------------------------------------------------------------
    INSERT INTO dbo.LoadEvidence_Finding (BatchId, MessageId, Severity, CheckName, EntityType, EntityKey, Detail)
    SELECT BatchId, MessageId, Severity, CheckName, EntityType, EntityKey, Detail
    FROM #Finding;

    UPDATE b
    SET ReferencesCheckedAt = SYSUTCDATETIME(),
        ErrorFindingCount = ISNULL(f.ErrorCount, 0),
        WarningFindingCount = ISNULL(f.WarningCount, 0),
        DependsOnBatchId = d.DependsOnBatchId
    FROM dbo.LoadEvidence_Batch b
    JOIN #Scope s ON s.BatchId = b.BatchId
    LEFT JOIN
    (
        SELECT BatchId,
               SUM(CASE WHEN Severity = 'Error' THEN 1 ELSE 0 END) AS ErrorCount,
               SUM(CASE WHEN Severity = 'Warning' THEN 1 ELSE 0 END) AS WarningCount
        FROM #Finding
        GROUP BY BatchId
    ) f ON f.BatchId = b.BatchId
    LEFT JOIN #Dep d ON d.BatchId = b.BatchId;

    COMMIT TRANSACTION;

    SELECT CheckName, Severity, COUNT(*) AS FindingCount
    FROM #Finding
    GROUP BY CheckName, Severity
    ORDER BY Severity, CheckName;
END;
GO
