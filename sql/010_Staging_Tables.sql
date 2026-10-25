-- 010: Inbox, staging, evidence and run-log tables in SapStaging. Safe to run more than once.
--
-- Design notes
-- * Inbox.RawXml is nvarchar(max), not xml. A malformed message must still be stored, or it would be
--   lost at the door. Each approach parses the text itself and records parse failures as evidence.
-- * Staging tables have no foreign keys and only one index (clustered on BatchId), so they load fast
--   and accept messages in any order. Every row carries MessageId and BatchId.
-- * Staging only holds rows from messages that passed validation (all-or-nothing per message).
USE SapStaging;
GO

IF SCHEMA_ID(N'stg') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA stg AUTHORIZATION dbo;');
END;
GO

------------------------------------------------------------------------------------------------
-- Inbox
------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Inbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Inbox
    (
        MessageId   varchar(50)   NOT NULL CONSTRAINT PK_Inbox PRIMARY KEY NONCLUSTERED,
        ArrivalSeq  bigint        IDENTITY(1, 1) NOT NULL,
        RawXml      nvarchar(max) NOT NULL,
        ReceivedAt  datetime2(3)  NOT NULL CONSTRAINT DF_Inbox_ReceivedAt DEFAULT (SYSUTCDATETIME()),
        BatchId     int           NULL,
        Status      varchar(20)   NOT NULL CONSTRAINT DF_Inbox_Status DEFAULT ('Received'),
        CONSTRAINT CK_Inbox_Status CHECK (Status IN ('Received', 'Batched', 'Passed', 'Failed'))
    );
    CREATE UNIQUE CLUSTERED INDEX CIX_Inbox_ArrivalSeq ON dbo.Inbox (ArrivalSeq);
    CREATE NONCLUSTERED INDEX IX_Inbox_BatchId ON dbo.Inbox (BatchId) INCLUDE (Status);
END;
GO

-- A second arrival of a MessageId that is already in the Inbox. The receiver skips it (idempotent)
-- but records it here, so nothing is dropped without a trace.
IF OBJECT_ID(N'dbo.InboxDuplicate', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InboxDuplicate
    (
        DuplicateId bigint        IDENTITY(1, 1) NOT NULL CONSTRAINT PK_InboxDuplicate PRIMARY KEY,
        MessageId   varchar(50)   NOT NULL,
        RawXml      nvarchar(max) NOT NULL,
        ReceivedAt  datetime2(3)  NOT NULL CONSTRAINT DF_InboxDuplicate_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
    CREATE NONCLUSTERED INDEX IX_InboxDuplicate_MessageId ON dbo.InboxDuplicate (MessageId);
END;
GO

------------------------------------------------------------------------------------------------
-- Staging tables (one per entity, plus child tables for repeating segments)
------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'stg.MessageHeader', N'U') IS NULL
BEGIN
    CREATE TABLE stg.MessageHeader
    (
        MessageId   varchar(50)  NOT NULL,
        BatchId     int          NOT NULL,
        MessageType varchar(30)  NOT NULL,
        CreatedAt   datetime2(0) NOT NULL,
        Sender      nvarchar(30) NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_MessageHeader_BatchId ON stg.MessageHeader (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Customer', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Customer
    (
        MessageId  varchar(50)   NOT NULL,
        BatchId    int           NOT NULL,
        CustomerId varchar(20)   NOT NULL,
        Category   varchar(10)   NOT NULL,
        FirstName  nvarchar(80)  NULL,
        LastName   nvarchar(80)  NULL,
        OrgName    nvarchar(120) NULL,
        Email      nvarchar(120) NULL,
        Phone      varchar(30)   NULL,
        ValidFrom  date          NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_Customer_BatchId ON stg.Customer (BatchId);
END;
GO

IF OBJECT_ID(N'stg.CustomerAddress', N'U') IS NULL
BEGIN
    CREATE TABLE stg.CustomerAddress
    (
        MessageId   varchar(50)   NOT NULL,
        BatchId     int           NOT NULL,
        CustomerId  varchar(20)   NOT NULL,
        AddressType varchar(10)   NOT NULL,
        Street      nvarchar(100) NOT NULL,
        HouseNo     nvarchar(10)  NULL,
        PostalCode  varchar(10)   NOT NULL,
        City        nvarchar(60)  NOT NULL,
        Country     char(2)       NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_CustomerAddress_BatchId ON stg.CustomerAddress (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Account', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Account
    (
        MessageId     varchar(50)   NOT NULL,
        BatchId       int           NOT NULL,
        AccountId     varchar(20)   NOT NULL,
        CustomerId    varchar(20)   NOT NULL,
        AccountClass  varchar(10)   NOT NULL,
        BillingCycle  varchar(10)   NOT NULL,
        PaymentMethod varchar(10)   NOT NULL,
        CreditLimit   decimal(18,2) NULL,
        OpenedOn      date          NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_Account_BatchId ON stg.Account (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Premise', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Premise
    (
        MessageId          varchar(50)   NOT NULL,
        BatchId            int           NOT NULL,
        PremiseId          varchar(20)   NOT NULL,
        ConnectionObjectId varchar(20)   NOT NULL,
        PremiseType        varchar(10)   NOT NULL,
        Street             nvarchar(100) NOT NULL,
        HouseNo            nvarchar(10)  NULL,
        Unit               nvarchar(10)  NULL,
        PostalCode         varchar(10)   NOT NULL,
        City               nvarchar(60)  NOT NULL,
        Country            char(2)       NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_Premise_BatchId ON stg.Premise (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Installation', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Installation
    (
        MessageId      varchar(50) NOT NULL,
        BatchId        int         NOT NULL,
        InstallationId varchar(20) NOT NULL,
        PremiseId      varchar(20) NOT NULL,
        Division       varchar(10) NOT NULL,
        RateCategory   varchar(10) NOT NULL,
        ValidFrom      date        NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_Installation_BatchId ON stg.Installation (BatchId);
END;
GO

-- One row per meter (device) installation period. A meter swap sends the removed meter
-- (with RemoveDate) and the newly installed meter.
IF OBJECT_ID(N'stg.Meter', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Meter
    (
        MessageId      varchar(50)  NOT NULL,
        BatchId        int          NOT NULL,
        MeterId        varchar(20)  NOT NULL,
        InstallationId varchar(20)  NOT NULL,
        SerialNumber   varchar(30)  NOT NULL,
        MeterType      varchar(10)  NOT NULL,
        Manufacturer   nvarchar(40) NULL,
        InstallDate    date         NOT NULL,
        RemoveDate     date         NULL
    );
    CREATE CLUSTERED INDEX CIX_Meter_BatchId ON stg.Meter (BatchId);
END;
GO

IF OBJECT_ID(N'stg.MeterRegister', N'U') IS NULL
BEGIN
    CREATE TABLE stg.MeterRegister
    (
        MessageId     varchar(50) NOT NULL,
        BatchId       int         NOT NULL,
        MeterId       varchar(20) NOT NULL,
        RegisterNo    varchar(10) NOT NULL,
        RegisterType  varchar(10) NOT NULL,
        UnitOfMeasure varchar(10) NOT NULL,
        Decimals      int         NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_MeterRegister_BatchId ON stg.MeterRegister (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Contract', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Contract
    (
        MessageId      varchar(50) NOT NULL,
        BatchId        int         NOT NULL,
        ContractId     varchar(20) NOT NULL,
        AccountId      varchar(20) NOT NULL,
        InstallationId varchar(20) NOT NULL,
        StartDate      date        NOT NULL,
        EndDate        date        NULL
    );
    CREATE CLUSTERED INDEX CIX_Contract_BatchId ON stg.Contract (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Agreement', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Agreement
    (
        MessageId     varchar(50)  NOT NULL,
        BatchId       int          NOT NULL,
        AgreementId   varchar(20)  NOT NULL,
        ContractId    varchar(20)  NOT NULL,
        AgreementType varchar(10)  NOT NULL,
        PriceLevel    varchar(10)  NOT NULL,
        DiscountPct   decimal(5,2) NULL,
        ValidFrom     date         NOT NULL,
        ValidTo       date         NULL
    );
    CREATE CLUSTERED INDEX CIX_Agreement_BatchId ON stg.Agreement (BatchId);
END;
GO

IF OBJECT_ID(N'stg.MoveInOut', N'U') IS NULL
BEGIN
    CREATE TABLE stg.MoveInOut
    (
        MessageId  varchar(50) NOT NULL,
        BatchId    int         NOT NULL,
        EventId    varchar(20) NOT NULL,
        EventType  varchar(10) NOT NULL,
        CustomerId varchar(20) NOT NULL,
        AccountId  varchar(20) NOT NULL,
        PremiseId  varchar(20) NOT NULL,
        EventDate  date        NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_MoveInOut_BatchId ON stg.MoveInOut (BatchId);
END;
GO

IF OBJECT_ID(N'stg.Payment', N'U') IS NULL
BEGIN
    CREATE TABLE stg.Payment
    (
        MessageId     varchar(50)   NOT NULL,
        BatchId       int           NOT NULL,
        PaymentId     varchar(20)   NOT NULL,
        AccountId     varchar(20)   NOT NULL,
        Amount        decimal(18,2) NOT NULL,
        Currency      char(3)       NOT NULL,
        PaymentDate   date          NOT NULL,
        PaymentMethod varchar(10)   NOT NULL,
        Reference     nvarchar(35)  NULL
    );
    CREATE CLUSTERED INDEX CIX_Payment_BatchId ON stg.Payment (BatchId);
END;
GO

------------------------------------------------------------------------------------------------
-- Evidence
------------------------------------------------------------------------------------------------
-- One row per message arrival. ArrivalNo 1 = the message itself; 2+ = duplicate arrivals.
IF OBJECT_ID(N'dbo.LoadEvidence_Message', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadEvidence_Message
    (
        MessageId        varchar(50)    NOT NULL,
        ArrivalNo        int            NOT NULL,
        BatchId          int            NOT NULL,
        MessageType      varchar(30)    NULL,
        ReceivedAt       datetime2(3)   NOT NULL,
        RawXmlHash       varbinary(32)  NOT NULL,
        ExpectedRowCount int            NULL,
        StagedRowCount   int            NOT NULL CONSTRAINT DF_LEM_StagedRowCount DEFAULT (0),
        ValidationStatus varchar(10)    NOT NULL,
        ErrorDetail      nvarchar(4000) NULL,
        ProcessedAt      datetime2(3)   NOT NULL CONSTRAINT DF_LEM_ProcessedAt DEFAULT (SYSUTCDATETIME()),
        Approach         char(1)        NOT NULL,
        CONSTRAINT PK_LoadEvidence_Message PRIMARY KEY NONCLUSTERED (MessageId, ArrivalNo),
        CONSTRAINT CK_LEM_ValidationStatus CHECK (ValidationStatus IN ('Passed', 'Failed', 'Duplicate')),
        CONSTRAINT CK_LEM_Approach CHECK (Approach IN ('A', 'B'))
    );
    CREATE CLUSTERED INDEX CIX_LoadEvidence_Message_BatchId ON dbo.LoadEvidence_Message (BatchId);
END;
GO

IF OBJECT_ID(N'dbo.LoadEvidence_Batch', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadEvidence_Batch
    (
        BatchId            int            NOT NULL CONSTRAINT PK_LoadEvidence_Batch PRIMARY KEY,
        MessageCount       int            NOT NULL,
        PassedCount        int            NOT NULL,
        FailedCount        int            NOT NULL,
        DuplicateCount     int            NOT NULL,
        StagedRowTotal     int            NOT NULL,
        PromotedRowTotal   int            NULL,
        Status             varchar(10)    NOT NULL,
        Approach           char(1)        NOT NULL,
        StagedAt           datetime2(3)   NOT NULL,
        ReferencesCheckedAt datetime2(3)  NULL,
        ErrorFindingCount  int            NULL,
        WarningFindingCount int           NULL,
        DependsOnBatchId   int            NULL,
        ApprovedBy         nvarchar(100)  NULL,
        ApprovedAt         datetime2(3)   NULL,
        ApprovalNote       nvarchar(400)  NULL,
        PromotedAt         datetime2(3)   NULL,
        RejectedBy         nvarchar(100)  NULL,
        RejectedAt         datetime2(3)   NULL,
        RejectReason       nvarchar(400)  NULL,
        CONSTRAINT CK_LEB_Status CHECK (Status IN ('Staged', 'Approved', 'Promoted', 'Rejected'))
    );
END;
GO

-- Cross-entity (referential and business-rule) findings. Each finding points to the message that
-- carried the row. Severity Error = the row cannot be promoted (it would break a target foreign key).
-- Severity Warning = the row breaks a business rule (for example overlapping periods); it blocks
-- auto-approval but may be promoted after manual approval.
IF OBJECT_ID(N'dbo.LoadEvidence_Finding', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadEvidence_Finding
    (
        FindingId  bigint         IDENTITY(1, 1) NOT NULL CONSTRAINT PK_LoadEvidence_Finding PRIMARY KEY,
        BatchId    int            NOT NULL,
        MessageId  varchar(50)    NOT NULL,
        Severity   varchar(10)    NOT NULL,
        CheckName  varchar(40)    NOT NULL,
        EntityType varchar(20)    NOT NULL,
        EntityKey  varchar(80)    NOT NULL,
        Detail     nvarchar(400)  NOT NULL,
        FoundAt    datetime2(3)   NOT NULL CONSTRAINT DF_LEF_FoundAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_LEF_Severity CHECK (Severity IN ('Error', 'Warning'))
    );
    CREATE NONCLUSTERED INDEX IX_LoadEvidence_Finding_Entity
        ON dbo.LoadEvidence_Finding (EntityType, EntityKey, MessageId) INCLUDE (Severity);
    CREATE NONCLUSTERED INDEX IX_LoadEvidence_Finding_BatchId ON dbo.LoadEvidence_Finding (BatchId);
END;
GO

-- What happened to every staged row at promotion, per batch and table.
-- StagedRows = PromotedRows + SupersededRows + ExcludedRows (checked by usp_PromoteBatches).
IF OBJECT_ID(N'dbo.LoadEvidence_Promotion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadEvidence_Promotion
    (
        BatchId        int          NOT NULL,
        TableName      varchar(30)  NOT NULL,
        StagedRows     int          NOT NULL,
        PromotedRows   int          NOT NULL,
        SupersededRows int          NOT NULL,
        ExcludedRows   int          NOT NULL,
        PromotedAt     datetime2(3) NOT NULL CONSTRAINT DF_LEP_PromotedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_LoadEvidence_Promotion PRIMARY KEY (BatchId, TableName)
    );
END;
GO

------------------------------------------------------------------------------------------------
-- Run log (used by both approaches and by the benchmark)
------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.LoadRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadRun
    (
        RunId            int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_LoadRun PRIMARY KEY,
        Approach         char(1)       NOT NULL,
        DegreeOfParallelism int        NOT NULL,
        StartedAt        datetime2(3)  NOT NULL,
        EndedAt          datetime2(3)  NULL,
        BatchCount       int           NULL,
        FailedBatchCount int           NULL,
        Note             nvarchar(200) NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.LoadRunBatch', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadRunBatch
    (
        RunId        int            NOT NULL,
        BatchId      int            NOT NULL,
        StartedAt    datetime2(3)   NOT NULL,
        EndedAt      datetime2(3)   NOT NULL,
        IsSucceeded  bit            NOT NULL,
        ErrorMessage nvarchar(4000) NULL,
        CONSTRAINT PK_LoadRunBatch PRIMARY KEY (RunId, BatchId)
    );
END;
GO

------------------------------------------------------------------------------------------------
-- Test manifest: written by the generator only. It lists every injected fault, so a test can
-- prove that the evidence caught each one. Not used by the load itself.
------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.TestManifest', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TestManifest
    (
        MessageId        varchar(50) NOT NULL,
        ArrivalNo        int         NOT NULL,
        MessageType      varchar(30) NOT NULL,
        FaultKind        varchar(30) NOT NULL,
        ExpectedRowCount int         NOT NULL,
        CONSTRAINT PK_TestManifest PRIMARY KEY (MessageId, ArrivalNo)
    );
END;
GO

------------------------------------------------------------------------------------------------
-- Row locks only on the tables that batches write side by side. With page locks, two batches
-- whose rows share a page could deadlock. Safe to run again.
------------------------------------------------------------------------------------------------
ALTER INDEX ALL ON dbo.Inbox SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON dbo.LoadEvidence_Message SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON dbo.LoadEvidence_Batch SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON dbo.LoadEvidence_Finding SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.MessageHeader SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Customer SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.CustomerAddress SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Account SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Premise SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Installation SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Meter SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.MeterRegister SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Contract SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Agreement SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.MoveInOut SET (ALLOW_PAGE_LOCKS = OFF);
ALTER INDEX ALL ON stg.Payment SET (ALLOW_PAGE_LOCKS = OFF);
GO
