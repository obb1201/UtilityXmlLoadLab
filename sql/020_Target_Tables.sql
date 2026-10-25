-- 020: Final tables in SapTarget, with primary and foreign keys. Safe to run more than once.
-- Every row keeps lineage: the message and batch it came from, and the message creation time
-- (used so an older message can never overwrite newer data).
USE SapTarget;
GO

IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customer
    (
        CustomerId      varchar(20)   NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
        Category        varchar(10)   NOT NULL,
        FirstName       nvarchar(80)  NULL,
        LastName        nvarchar(80)  NULL,
        OrgName         nvarchar(120) NULL,
        Email           nvarchar(120) NULL,
        Phone           varchar(30)   NULL,
        ValidFrom       date          NOT NULL,
        SourceMessageId varchar(50)   NOT NULL,
        SourceBatchId   int           NOT NULL,
        SourceCreatedAt datetime2(0)  NOT NULL,
        PromotedAt      datetime2(3)  NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.CustomerAddress', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerAddress
    (
        CustomerId      varchar(20)   NOT NULL,
        AddressType     varchar(10)   NOT NULL,
        Street          nvarchar(100) NOT NULL,
        HouseNo         nvarchar(10)  NULL,
        PostalCode      varchar(10)   NOT NULL,
        City            nvarchar(60)  NOT NULL,
        Country         char(2)       NOT NULL,
        SourceMessageId varchar(50)   NOT NULL,
        SourceBatchId   int           NOT NULL,
        SourceCreatedAt datetime2(0)  NOT NULL,
        PromotedAt      datetime2(3)  NOT NULL,
        CONSTRAINT PK_CustomerAddress PRIMARY KEY (CustomerId, AddressType),
        CONSTRAINT FK_CustomerAddress_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId)
    );
END;
GO

IF OBJECT_ID(N'dbo.Account', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Account
    (
        AccountId       varchar(20)   NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
        CustomerId      varchar(20)   NOT NULL,
        AccountClass    varchar(10)   NOT NULL,
        BillingCycle    varchar(10)   NOT NULL,
        PaymentMethod   varchar(10)   NOT NULL,
        CreditLimit     decimal(18,2) NULL,
        OpenedOn        date          NOT NULL,
        SourceMessageId varchar(50)   NOT NULL,
        SourceBatchId   int           NOT NULL,
        SourceCreatedAt datetime2(0)  NOT NULL,
        PromotedAt      datetime2(3)  NOT NULL,
        CONSTRAINT FK_Account_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId)
    );
    CREATE NONCLUSTERED INDEX IX_Account_CustomerId ON dbo.Account (CustomerId);
END;
GO

IF OBJECT_ID(N'dbo.Premise', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Premise
    (
        PremiseId          varchar(20)   NOT NULL CONSTRAINT PK_Premise PRIMARY KEY,
        ConnectionObjectId varchar(20)   NOT NULL,
        PremiseType        varchar(10)   NOT NULL,
        Street             nvarchar(100) NOT NULL,
        HouseNo            nvarchar(10)  NULL,
        Unit               nvarchar(10)  NULL,
        PostalCode         varchar(10)   NOT NULL,
        City               nvarchar(60)  NOT NULL,
        Country            char(2)       NOT NULL,
        SourceMessageId    varchar(50)   NOT NULL,
        SourceBatchId      int           NOT NULL,
        SourceCreatedAt    datetime2(0)  NOT NULL,
        PromotedAt         datetime2(3)  NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.Installation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Installation
    (
        InstallationId  varchar(20)  NOT NULL CONSTRAINT PK_Installation PRIMARY KEY,
        PremiseId       varchar(20)  NOT NULL,
        Division        varchar(10)  NOT NULL,
        RateCategory    varchar(10)  NOT NULL,
        ValidFrom       date         NOT NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT FK_Installation_Premise FOREIGN KEY (PremiseId) REFERENCES dbo.Premise (PremiseId)
    );
    CREATE NONCLUSTERED INDEX IX_Installation_PremiseId ON dbo.Installation (PremiseId);
END;
GO

-- The meter (device) itself.
IF OBJECT_ID(N'dbo.Meter', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Meter
    (
        MeterId         varchar(20)  NOT NULL CONSTRAINT PK_Meter PRIMARY KEY,
        SerialNumber    varchar(30)  NOT NULL,
        MeterType       varchar(10)  NOT NULL,
        Manufacturer    nvarchar(40) NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL
    );
END;
GO

-- When and where a meter was installed.
IF OBJECT_ID(N'dbo.MeterInstall', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeterInstall
    (
        MeterId         varchar(20)  NOT NULL,
        InstallationId  varchar(20)  NOT NULL,
        InstallDate     date         NOT NULL,
        RemoveDate      date         NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT PK_MeterInstall PRIMARY KEY (MeterId, InstallationId, InstallDate),
        CONSTRAINT FK_MeterInstall_Meter FOREIGN KEY (MeterId) REFERENCES dbo.Meter (MeterId),
        CONSTRAINT FK_MeterInstall_Installation FOREIGN KEY (InstallationId) REFERENCES dbo.Installation (InstallationId)
    );
    CREATE NONCLUSTERED INDEX IX_MeterInstall_InstallationId ON dbo.MeterInstall (InstallationId);
END;
GO

IF OBJECT_ID(N'dbo.MeterRegister', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeterRegister
    (
        MeterId         varchar(20)  NOT NULL,
        RegisterNo      varchar(10)  NOT NULL,
        RegisterType    varchar(10)  NOT NULL,
        UnitOfMeasure   varchar(10)  NOT NULL,
        Decimals        int          NOT NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT PK_MeterRegister PRIMARY KEY (MeterId, RegisterNo),
        CONSTRAINT FK_MeterRegister_Meter FOREIGN KEY (MeterId) REFERENCES dbo.Meter (MeterId)
    );
END;
GO

IF OBJECT_ID(N'dbo.Contract', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Contract
    (
        ContractId      varchar(20)  NOT NULL CONSTRAINT PK_Contract PRIMARY KEY,
        AccountId       varchar(20)  NOT NULL,
        InstallationId  varchar(20)  NOT NULL,
        StartDate       date         NOT NULL,
        EndDate         date         NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT FK_Contract_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account (AccountId),
        CONSTRAINT FK_Contract_Installation FOREIGN KEY (InstallationId) REFERENCES dbo.Installation (InstallationId)
    );
    CREATE NONCLUSTERED INDEX IX_Contract_AccountId ON dbo.Contract (AccountId);
    CREATE NONCLUSTERED INDEX IX_Contract_InstallationId ON dbo.Contract (InstallationId);
END;
GO

IF OBJECT_ID(N'dbo.Agreement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Agreement
    (
        AgreementId     varchar(20)  NOT NULL CONSTRAINT PK_Agreement PRIMARY KEY,
        ContractId      varchar(20)  NOT NULL,
        AgreementType   varchar(10)  NOT NULL,
        PriceLevel      varchar(10)  NOT NULL,
        DiscountPct     decimal(5,2) NULL,
        ValidFrom       date         NOT NULL,
        ValidTo         date         NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT FK_Agreement_Contract FOREIGN KEY (ContractId) REFERENCES dbo.Contract (ContractId)
    );
    CREATE NONCLUSTERED INDEX IX_Agreement_ContractId ON dbo.Agreement (ContractId);
END;
GO

IF OBJECT_ID(N'dbo.MoveInOut', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MoveInOut
    (
        EventId         varchar(20)  NOT NULL CONSTRAINT PK_MoveInOut PRIMARY KEY,
        EventType       varchar(10)  NOT NULL,
        CustomerId      varchar(20)  NOT NULL,
        AccountId       varchar(20)  NOT NULL,
        PremiseId       varchar(20)  NOT NULL,
        EventDate       date         NOT NULL,
        SourceMessageId varchar(50)  NOT NULL,
        SourceBatchId   int          NOT NULL,
        SourceCreatedAt datetime2(0) NOT NULL,
        PromotedAt      datetime2(3) NOT NULL,
        CONSTRAINT FK_MoveInOut_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId),
        CONSTRAINT FK_MoveInOut_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account (AccountId),
        CONSTRAINT FK_MoveInOut_Premise FOREIGN KEY (PremiseId) REFERENCES dbo.Premise (PremiseId)
    );
END;
GO

IF OBJECT_ID(N'dbo.Payment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Payment
    (
        PaymentId       varchar(20)   NOT NULL CONSTRAINT PK_Payment PRIMARY KEY,
        AccountId       varchar(20)   NOT NULL,
        Amount          decimal(18,2) NOT NULL,
        Currency        char(3)       NOT NULL,
        PaymentDate     date          NOT NULL,
        PaymentMethod   varchar(10)   NOT NULL,
        Reference       nvarchar(35)  NULL,
        SourceMessageId varchar(50)   NOT NULL,
        SourceBatchId   int           NOT NULL,
        SourceCreatedAt datetime2(0)  NOT NULL,
        PromotedAt      datetime2(3)  NOT NULL,
        CONSTRAINT FK_Payment_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account (AccountId)
    );
    CREATE NONCLUSTERED INDEX IX_Payment_AccountId ON dbo.Payment (AccountId);
END;
GO
