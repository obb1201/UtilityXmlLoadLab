-- 050: Approach A - shred one batch from the Inbox with T-SQL (xml.nodes + nested CROSS APPLY).
-- Safe to run more than once.
--
-- How it works (one transaction per batch):
--   1. Parse each message once with TRY_CONVERT(xml, ...). NULL = not well-formed.
--   2. Shred every segment into a temp table as raw text (nvarchar). No conversion yet, so a bad
--      value cannot stop the batch. Parent keys come from the outer CROSS APPLY node
--      (no parent axis "..").
--   3. Validate every field with dbo.fn_FieldProblem, plus message-level checks.
--   4. Insert typed rows into staging, only for messages with no problems (all-or-nothing).
--   5. Write one evidence row per message, then call dbo.usp_FinalizeBatch.
--
-- The XML-to-table mapping lives here and in StagingMap.cs (approach B). If you change one,
-- change the other; the benchmark compares the output of both and reports any difference.
-- The XML statements use OPTION (MAXDOP 1): a parallel plan for xml.nodes() over one batch was
-- about 15 times slower (exchange overhead). Parallelism comes from running batches side by side.
USE SapStaging;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ShredBatch
    @BatchId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    EXEC dbo.usp_BeginBatch @BatchId = @BatchId;

    --------------------------------------------------------------------------------------------
    -- 1. Messages of the batch, parsed once.
    --------------------------------------------------------------------------------------------
    CREATE TABLE #Msg
    (
        MessageId        varchar(50)   NOT NULL PRIMARY KEY,
        ReceivedAt       datetime2(3)  NOT NULL,
        RawXmlHash       varbinary(32) NOT NULL,
        Doc              xml           NULL,
        ExpectedRowCount int           NULL,
        MessageType      nvarchar(400) NULL,
        IsPassed         bit           NOT NULL DEFAULT (0)
    );

    INSERT INTO #Msg (MessageId, ReceivedAt, RawXmlHash, Doc)
    SELECT i.MessageId, i.ReceivedAt, HASHBYTES('SHA2_256', i.RawXml), TRY_CONVERT(xml, i.RawXml)
    FROM dbo.Inbox i
    WHERE i.BatchId = @BatchId;

    -- Expected rows = every segment element anywhere in the message, counted independently of the
    -- mapping below. A segment in an unexpected place is counted here but not mapped, so the
    -- message fails instead of silently losing that segment. "*:" = in any namespace.
    UPDATE #Msg
    SET ExpectedRowCount = Doc.value('count(//*:Header) + count(//*:Customer) + count(//*:Address) + count(//*:Account) + count(//*:Premise)
        + count(//*:Installation) + count(//*:Meter) + count(//*:RemovedMeter) + count(//*:InstalledMeter) + count(//*:Register)
        + count(//*:MoveEvent) + count(//*:Contract) + count(//*:Agreement) + count(//*:Payment)', 'int')
    WHERE Doc IS NOT NULL
    OPTION (MAXDOP 1);

    --------------------------------------------------------------------------------------------
    -- 2. Shred each segment as raw text.
    --------------------------------------------------------------------------------------------
    SELECT m.MessageId,
           h.n.value('(MessageId/text())[1]',   'nvarchar(400)') AS HeaderMessageId,
           h.n.value('(MessageType/text())[1]', 'nvarchar(400)') AS MessageType,
           h.n.value('(CreatedAt/text())[1]',   'nvarchar(400)') AS CreatedAt,
           h.n.value('(Sender/text())[1]',      'nvarchar(400)') AS Sender
    INTO #Header
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Header') h(n)
    OPTION (MAXDOP 1);

    -- Each body segment is shredded only from the message types that carry it (see StagingMap.cs).
    -- Every nodes() call reads every document, so this saves most of the work. It stays safe: a
    -- segment in another message type is still counted in ExpectedRowCount, so the message fails.
    UPDATE m
    SET MessageType = (SELECT TOP (1) h.MessageType FROM #Header h WHERE h.MessageId = m.MessageId)
    FROM #Msg m;

    SELECT m.MessageId,
           c.n.value('(CustomerId/text())[1]', 'nvarchar(400)') AS CustomerId,
           c.n.value('(Category/text())[1]',   'nvarchar(400)') AS Category,
           c.n.value('(FirstName/text())[1]',  'nvarchar(400)') AS FirstName,
           c.n.value('(LastName/text())[1]',   'nvarchar(400)') AS LastName,
           c.n.value('(OrgName/text())[1]',    'nvarchar(400)') AS OrgName,
           c.n.value('(Email/text())[1]',      'nvarchar(400)') AS Email,
           c.n.value('(Phone/text())[1]',      'nvarchar(400)') AS Phone,
           c.n.value('(ValidFrom/text())[1]',  'nvarchar(400)') AS ValidFrom
    INTO #Customer
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Customer') c(n)
    WHERE m.MessageType = N'CUSTOMER_MASTER'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           c.n.value('(CustomerId/text())[1]',  'nvarchar(400)') AS CustomerId,
           a.n.value('(AddressType/text())[1]', 'nvarchar(400)') AS AddressType,
           a.n.value('(Street/text())[1]',      'nvarchar(400)') AS Street,
           a.n.value('(HouseNo/text())[1]',     'nvarchar(400)') AS HouseNo,
           a.n.value('(PostalCode/text())[1]',  'nvarchar(400)') AS PostalCode,
           a.n.value('(City/text())[1]',        'nvarchar(400)') AS City,
           a.n.value('(Country/text())[1]',     'nvarchar(400)') AS Country
    INTO #CustomerAddress
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Customer') c(n)
    CROSS APPLY c.n.nodes('Address') a(n)
    WHERE m.MessageType = N'CUSTOMER_MASTER'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           a.n.value('(AccountId/text())[1]',     'nvarchar(400)') AS AccountId,
           c.n.value('(CustomerId/text())[1]',    'nvarchar(400)') AS CustomerId,
           a.n.value('(AccountClass/text())[1]',  'nvarchar(400)') AS AccountClass,
           a.n.value('(BillingCycle/text())[1]',  'nvarchar(400)') AS BillingCycle,
           a.n.value('(PaymentMethod/text())[1]', 'nvarchar(400)') AS PaymentMethod,
           a.n.value('(CreditLimit/text())[1]',   'nvarchar(400)') AS CreditLimit,
           a.n.value('(OpenedOn/text())[1]',      'nvarchar(400)') AS OpenedOn
    INTO #Account
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Customer') c(n)
    CROSS APPLY c.n.nodes('Account') a(n)
    WHERE m.MessageType = N'CUSTOMER_MASTER'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           p.n.value('(PremiseId/text())[1]',          'nvarchar(400)') AS PremiseId,
           p.n.value('(ConnectionObjectId/text())[1]', 'nvarchar(400)') AS ConnectionObjectId,
           p.n.value('(PremiseType/text())[1]',        'nvarchar(400)') AS PremiseType,
           p.n.value('(Street/text())[1]',             'nvarchar(400)') AS Street,
           p.n.value('(HouseNo/text())[1]',            'nvarchar(400)') AS HouseNo,
           p.n.value('(Unit/text())[1]',               'nvarchar(400)') AS Unit,
           p.n.value('(PostalCode/text())[1]',         'nvarchar(400)') AS PostalCode,
           p.n.value('(City/text())[1]',               'nvarchar(400)') AS City,
           p.n.value('(Country/text())[1]',            'nvarchar(400)') AS Country
    INTO #Premise
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Premise') p(n)
    WHERE m.MessageType = N'PREMISE_MASTER'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           i.n.value('(InstallationId/text())[1]', 'nvarchar(400)') AS InstallationId,
           p.n.value('(PremiseId/text())[1]',      'nvarchar(400)') AS PremiseId,
           i.n.value('(Division/text())[1]',       'nvarchar(400)') AS Division,
           i.n.value('(RateCategory/text())[1]',   'nvarchar(400)') AS RateCategory,
           i.n.value('(ValidFrom/text())[1]',      'nvarchar(400)') AS ValidFrom
    INTO #Installation
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Premise') p(n)
    CROSS APPLY p.n.nodes('Installation') i(n)
    WHERE m.MessageType = N'PREMISE_MASTER'
    OPTION (MAXDOP 1);

    -- Meters come from three places: installed with a new premise, and removed/installed in a swap.
    -- The InstallationId comes from the enclosing Installation or MeterSwap node.
    SELECT m.MessageId, CAST('Meter' AS varchar(20)) AS SegmentName,
           i.n.value('(InstallationId/text())[1]', 'nvarchar(400)') AS InstallationId,
           mt.n.value('(MeterId/text())[1]',      'nvarchar(400)') AS MeterId,
           mt.n.value('(SerialNumber/text())[1]', 'nvarchar(400)') AS SerialNumber,
           mt.n.value('(MeterType/text())[1]',    'nvarchar(400)') AS MeterType,
           mt.n.value('(Manufacturer/text())[1]', 'nvarchar(400)') AS Manufacturer,
           mt.n.value('(InstallDate/text())[1]',  'nvarchar(400)') AS InstallDate,
           mt.n.value('(RemoveDate/text())[1]',   'nvarchar(400)') AS RemoveDate
    INTO #Meter
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Premise/Installation') i(n)
    CROSS APPLY i.n.nodes('Meter') mt(n)
    WHERE m.MessageType = N'PREMISE_MASTER'
    UNION ALL
    SELECT m.MessageId, 'RemovedMeter',
           s.n.value('(InstallationId/text())[1]', 'nvarchar(400)'),
           mt.n.value('(MeterId/text())[1]',      'nvarchar(400)') AS MeterId,
           mt.n.value('(SerialNumber/text())[1]', 'nvarchar(400)') AS SerialNumber,
           mt.n.value('(MeterType/text())[1]',    'nvarchar(400)') AS MeterType,
           mt.n.value('(Manufacturer/text())[1]', 'nvarchar(400)') AS Manufacturer,
           mt.n.value('(InstallDate/text())[1]',  'nvarchar(400)') AS InstallDate,
           mt.n.value('(RemoveDate/text())[1]',   'nvarchar(400)') AS RemoveDate
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/MeterSwap') s(n)
    CROSS APPLY s.n.nodes('RemovedMeter') mt(n)
    WHERE m.MessageType = N'METER_SWAP'
    UNION ALL
    SELECT m.MessageId, 'InstalledMeter',
           s.n.value('(InstallationId/text())[1]', 'nvarchar(400)'),
           mt.n.value('(MeterId/text())[1]',      'nvarchar(400)') AS MeterId,
           mt.n.value('(SerialNumber/text())[1]', 'nvarchar(400)') AS SerialNumber,
           mt.n.value('(MeterType/text())[1]',    'nvarchar(400)') AS MeterType,
           mt.n.value('(Manufacturer/text())[1]', 'nvarchar(400)') AS Manufacturer,
           mt.n.value('(InstallDate/text())[1]',  'nvarchar(400)') AS InstallDate,
           mt.n.value('(RemoveDate/text())[1]',   'nvarchar(400)') AS RemoveDate
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/MeterSwap') s(n)
    CROSS APPLY s.n.nodes('InstalledMeter') mt(n)
    WHERE m.MessageType = N'METER_SWAP'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           mt.n.value('(MeterId/text())[1]', 'nvarchar(400)') AS MeterId,
           r.n.value('(RegisterNo/text())[1]',    'nvarchar(400)') AS RegisterNo,
           r.n.value('(RegisterType/text())[1]',  'nvarchar(400)') AS RegisterType,
           r.n.value('(UnitOfMeasure/text())[1]', 'nvarchar(400)') AS UnitOfMeasure,
           r.n.value('(Decimals/text())[1]',      'nvarchar(400)') AS Decimals
    INTO #MeterRegister
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Premise/Installation/Meter') mt(n)
    CROSS APPLY mt.n.nodes('Register') r(n)
    WHERE m.MessageType = N'PREMISE_MASTER'
    UNION ALL
    SELECT m.MessageId,
           mt.n.value('(MeterId/text())[1]', 'nvarchar(400)'),
           r.n.value('(RegisterNo/text())[1]',    'nvarchar(400)') AS RegisterNo,
           r.n.value('(RegisterType/text())[1]',  'nvarchar(400)') AS RegisterType,
           r.n.value('(UnitOfMeasure/text())[1]', 'nvarchar(400)') AS UnitOfMeasure,
           r.n.value('(Decimals/text())[1]',      'nvarchar(400)') AS Decimals
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/MeterSwap/RemovedMeter') mt(n)
    CROSS APPLY mt.n.nodes('Register') r(n)
    WHERE m.MessageType = N'METER_SWAP'
    UNION ALL
    SELECT m.MessageId,
           mt.n.value('(MeterId/text())[1]', 'nvarchar(400)'),
           r.n.value('(RegisterNo/text())[1]',    'nvarchar(400)') AS RegisterNo,
           r.n.value('(RegisterType/text())[1]',  'nvarchar(400)') AS RegisterType,
           r.n.value('(UnitOfMeasure/text())[1]', 'nvarchar(400)') AS UnitOfMeasure,
           r.n.value('(Decimals/text())[1]',      'nvarchar(400)') AS Decimals
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/MeterSwap/InstalledMeter') mt(n)
    CROSS APPLY mt.n.nodes('Register') r(n)
    WHERE m.MessageType = N'METER_SWAP'
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           c.n.value('(ContractId/text())[1]',     'nvarchar(400)') AS ContractId,
           c.n.value('(AccountId/text())[1]',      'nvarchar(400)') AS AccountId,
           c.n.value('(InstallationId/text())[1]', 'nvarchar(400)') AS InstallationId,
           c.n.value('(StartDate/text())[1]',      'nvarchar(400)') AS StartDate,
           c.n.value('(EndDate/text())[1]',        'nvarchar(400)') AS EndDate
    INTO #Contract
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Contract') c(n)
    WHERE m.MessageType IN (N'MOVE_IN', N'MOVE_OUT')
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           a.n.value('(AgreementId/text())[1]',   'nvarchar(400)') AS AgreementId,
           c.n.value('(ContractId/text())[1]',    'nvarchar(400)') AS ContractId,
           a.n.value('(AgreementType/text())[1]', 'nvarchar(400)') AS AgreementType,
           a.n.value('(PriceLevel/text())[1]',    'nvarchar(400)') AS PriceLevel,
           a.n.value('(DiscountPct/text())[1]',   'nvarchar(400)') AS DiscountPct,
           a.n.value('(ValidFrom/text())[1]',     'nvarchar(400)') AS ValidFrom,
           a.n.value('(ValidTo/text())[1]',       'nvarchar(400)') AS ValidTo
    INTO #Agreement
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Contract') c(n)
    CROSS APPLY c.n.nodes('Agreement') a(n)
    WHERE m.MessageType IN (N'MOVE_IN', N'MOVE_OUT')
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           e.n.value('(EventId/text())[1]',    'nvarchar(400)') AS EventId,
           e.n.value('(EventType/text())[1]',  'nvarchar(400)') AS EventType,
           e.n.value('(CustomerId/text())[1]', 'nvarchar(400)') AS CustomerId,
           e.n.value('(AccountId/text())[1]',  'nvarchar(400)') AS AccountId,
           e.n.value('(PremiseId/text())[1]',  'nvarchar(400)') AS PremiseId,
           e.n.value('(EventDate/text())[1]',  'nvarchar(400)') AS EventDate
    INTO #MoveInOut
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/MoveEvent') e(n)
    WHERE m.MessageType IN (N'MOVE_IN', N'MOVE_OUT')
    OPTION (MAXDOP 1);

    SELECT m.MessageId,
           p.n.value('(PaymentId/text())[1]',     'nvarchar(400)') AS PaymentId,
           p.n.value('(AccountId/text())[1]',     'nvarchar(400)') AS AccountId,
           p.n.value('(Amount/text())[1]',        'nvarchar(400)') AS Amount,
           p.n.value('(Currency/text())[1]',      'nvarchar(400)') AS Currency,
           p.n.value('(PaymentDate/text())[1]',   'nvarchar(400)') AS PaymentDate,
           p.n.value('(PaymentMethod/text())[1]', 'nvarchar(400)') AS PaymentMethod,
           p.n.value('(Reference/text())[1]',     'nvarchar(400)') AS Reference
    INTO #Payment
    FROM #Msg m
    CROSS APPLY m.Doc.nodes('/SapMessage/Body/Payment') p(n)
    WHERE m.MessageType = N'PAYMENT_BATCH'
    OPTION (MAXDOP 1);

    --------------------------------------------------------------------------------------------
    -- 3. Validation. Each problem becomes one row in #Err.
    --------------------------------------------------------------------------------------------
    CREATE TABLE #Err
    (
        ErrorSeq  int IDENTITY(1, 1) NOT NULL,
        MessageId varchar(50)        NOT NULL,
        ErrorText nvarchar(400)      NOT NULL
    );

    -- Message-level checks.
    INSERT INTO #Err (MessageId, ErrorText)
    SELECT m.MessageId, N'Malformed XML: the message is not well-formed.'
    FROM #Msg m
    WHERE m.Doc IS NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT m.MessageId,
           CASE WHEN hc.HeaderCount = 0 THEN N'Header is missing.'
                ELSE N'Header appears more than once.'
           END
    FROM #Msg m
    CROSS APPLY (SELECT COUNT(*) AS HeaderCount FROM #Header h WHERE h.MessageId = m.MessageId) hc
    WHERE m.Doc IS NOT NULL AND hc.HeaderCount <> 1;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT h.MessageId, CONCAT(N'Header: ', p.Problem)
    FROM #Header h
    CROSS APPLY (VALUES
        (CASE WHEN h.HeaderMessageId IS NULL THEN N'MessageId is missing'
              WHEN h.HeaderMessageId COLLATE Latin1_General_BIN2 <> h.MessageId THEN N'MessageId does not match the Inbox MessageId'
         END),
        (CASE WHEN h.MessageType IS NULL THEN N'MessageType is missing'
              WHEN h.MessageType COLLATE Latin1_General_BIN2 NOT IN (N'CUSTOMER_MASTER', N'PREMISE_MASTER', N'MOVE_IN', N'MOVE_OUT',
                                         N'METER_SWAP', N'PAYMENT_BATCH')
                  THEN CONCAT(N'MessageType ''', LEFT(h.MessageType, 40), N''' is unknown')
         END),
        (dbo.fn_FieldProblem(h.CreatedAt, 'CreatedAt', 'DT', 0, 1)),
        (dbo.fn_FieldProblem(h.Sender,    'Sender',    'T', 30, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    -- Field checks per segment: (value, name, type, max length, required).
    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Customer ', ISNULL(LEFT(s.CustomerId, 40), N'?'), N': ', p.Problem)
    FROM #Customer s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.CustomerId, 'CustomerId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.Category,   'Category',   'T', 10, 1)),
        (dbo.fn_FieldProblem(s.FirstName,  'FirstName',  'T', 80, 0)),
        (dbo.fn_FieldProblem(s.LastName,   'LastName',   'T', 80, 0)),
        (dbo.fn_FieldProblem(s.OrgName,    'OrgName',    'T', 120, 0)),
        (dbo.fn_FieldProblem(s.Email,      'Email',      'T', 120, 0)),
        (dbo.fn_FieldProblem(s.Phone,      'Phone',      'T', 30, 0)),
        (dbo.fn_FieldProblem(s.ValidFrom,  'ValidFrom',  'D', 0, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Address ', ISNULL(LEFT(s.CustomerId, 40), N'?'), N': ', p.Problem)
    FROM #CustomerAddress s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.CustomerId,  'CustomerId',  'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AddressType, 'AddressType', 'T', 10, 1)),
        (dbo.fn_FieldProblem(s.Street,      'Street',      'T', 100, 1)),
        (dbo.fn_FieldProblem(s.HouseNo,     'HouseNo',     'T', 10, 0)),
        (dbo.fn_FieldProblem(s.PostalCode,  'PostalCode',  'T', 10, 1)),
        (dbo.fn_FieldProblem(s.City,        'City',        'T', 60, 1)),
        (dbo.fn_FieldProblem(s.Country,     'Country',     'T', 2, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Account ', ISNULL(LEFT(s.AccountId, 40), N'?'), N': ', p.Problem)
    FROM #Account s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.AccountId,     'AccountId',     'T', 20, 1)),
        (dbo.fn_FieldProblem(s.CustomerId,    'CustomerId',    'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AccountClass,  'AccountClass',  'T', 10, 1)),
        (dbo.fn_FieldProblem(s.BillingCycle,  'BillingCycle',  'T', 10, 1)),
        (dbo.fn_FieldProblem(s.PaymentMethod, 'PaymentMethod', 'T', 10, 1)),
        (dbo.fn_FieldProblem(s.CreditLimit,   'CreditLimit',   'N', 0, 0)),
        (dbo.fn_FieldProblem(s.OpenedOn,      'OpenedOn',      'D', 0, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Premise ', ISNULL(LEFT(s.PremiseId, 40), N'?'), N': ', p.Problem)
    FROM #Premise s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.PremiseId,          'PremiseId',          'T', 20, 1)),
        (dbo.fn_FieldProblem(s.ConnectionObjectId, 'ConnectionObjectId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.PremiseType,        'PremiseType',        'T', 10, 1)),
        (dbo.fn_FieldProblem(s.Street,             'Street',             'T', 100, 1)),
        (dbo.fn_FieldProblem(s.HouseNo,            'HouseNo',            'T', 10, 0)),
        (dbo.fn_FieldProblem(s.Unit,               'Unit',               'T', 10, 0)),
        (dbo.fn_FieldProblem(s.PostalCode,         'PostalCode',         'T', 10, 1)),
        (dbo.fn_FieldProblem(s.City,               'City',               'T', 60, 1)),
        (dbo.fn_FieldProblem(s.Country,            'Country',            'T', 2, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Installation ', ISNULL(LEFT(s.InstallationId, 40), N'?'), N': ', p.Problem)
    FROM #Installation s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.InstallationId, 'InstallationId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.PremiseId,      'PremiseId',      'T', 20, 1)),
        (dbo.fn_FieldProblem(s.Division,       'Division',       'T', 10, 1)),
        (dbo.fn_FieldProblem(s.RateCategory,   'RateCategory',   'T', 10, 1)),
        (dbo.fn_FieldProblem(s.ValidFrom,      'ValidFrom',      'D', 0, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(s.SegmentName, N' ', ISNULL(LEFT(s.MeterId, 40), N'?'), N': ', p.Problem)
    FROM #Meter s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.MeterId,        'MeterId',        'T', 20, 1)),
        (dbo.fn_FieldProblem(s.InstallationId, 'InstallationId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.SerialNumber,   'SerialNumber',   'T', 30, 1)),
        (dbo.fn_FieldProblem(s.MeterType,      'MeterType',      'T', 10, 1)),
        (dbo.fn_FieldProblem(s.Manufacturer,   'Manufacturer',   'T', 40, 0)),
        (dbo.fn_FieldProblem(s.InstallDate,    'InstallDate',    'D', 0, 1)),
        (dbo.fn_FieldProblem(s.RemoveDate,     'RemoveDate',     'D', 0, 0))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Register ', ISNULL(LEFT(s.MeterId, 40), N'?'), N': ', p.Problem)
    FROM #MeterRegister s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.MeterId,       'MeterId',       'T', 20, 1)),
        (dbo.fn_FieldProblem(s.RegisterNo,    'RegisterNo',    'T', 10, 1)),
        (dbo.fn_FieldProblem(s.RegisterType,  'RegisterType',  'T', 10, 1)),
        (dbo.fn_FieldProblem(s.UnitOfMeasure, 'UnitOfMeasure', 'T', 10, 1)),
        (dbo.fn_FieldProblem(s.Decimals,      'Decimals',      'I', 0, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Contract ', ISNULL(LEFT(s.ContractId, 40), N'?'), N': ', p.Problem)
    FROM #Contract s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.ContractId,     'ContractId',     'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AccountId,      'AccountId',      'T', 20, 1)),
        (dbo.fn_FieldProblem(s.InstallationId, 'InstallationId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.StartDate,      'StartDate',      'D', 0, 1)),
        (dbo.fn_FieldProblem(s.EndDate,        'EndDate',        'D', 0, 0))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Agreement ', ISNULL(LEFT(s.AgreementId, 40), N'?'), N': ', p.Problem)
    FROM #Agreement s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.AgreementId,   'AgreementId',   'T', 20, 1)),
        (dbo.fn_FieldProblem(s.ContractId,    'ContractId',    'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AgreementType, 'AgreementType', 'T', 10, 1)),
        (dbo.fn_FieldProblem(s.PriceLevel,    'PriceLevel',    'T', 10, 1)),
        (dbo.fn_FieldProblem(s.DiscountPct,   'DiscountPct',   'P', 0, 0)),
        (dbo.fn_FieldProblem(s.ValidFrom,     'ValidFrom',     'D', 0, 1)),
        (dbo.fn_FieldProblem(s.ValidTo,       'ValidTo',       'D', 0, 0))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'MoveEvent ', ISNULL(LEFT(s.EventId, 40), N'?'), N': ', p.Problem)
    FROM #MoveInOut s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.EventId,    'EventId',    'T', 20, 1)),
        (CASE WHEN s.EventType IS NULL THEN N'EventType is missing'
              WHEN s.EventType COLLATE Latin1_General_BIN2 NOT IN (N'MOVE_IN', N'MOVE_OUT')
                  THEN CONCAT(N'EventType ''', LEFT(s.EventType, 40), N''' is unknown')
         END),
        (dbo.fn_FieldProblem(s.CustomerId, 'CustomerId', 'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AccountId,  'AccountId',  'T', 20, 1)),
        (dbo.fn_FieldProblem(s.PremiseId,  'PremiseId',  'T', 20, 1)),
        (dbo.fn_FieldProblem(s.EventDate,  'EventDate',  'D', 0, 1))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    INSERT INTO #Err (MessageId, ErrorText)
    SELECT s.MessageId, CONCAT(N'Payment ', ISNULL(LEFT(s.PaymentId, 40), N'?'), N': ', p.Problem)
    FROM #Payment s
    CROSS APPLY (VALUES
        (dbo.fn_FieldProblem(s.PaymentId,     'PaymentId',     'T', 20, 1)),
        (dbo.fn_FieldProblem(s.AccountId,     'AccountId',     'T', 20, 1)),
        (dbo.fn_FieldProblem(s.Amount,        'Amount',        'N', 0, 1)),
        (dbo.fn_FieldProblem(s.Currency,      'Currency',      'T', 3, 1)),
        (dbo.fn_FieldProblem(s.PaymentDate,   'PaymentDate',   'D', 0, 1)),
        (dbo.fn_FieldProblem(s.PaymentMethod, 'PaymentMethod', 'T', 10, 1)),
        (dbo.fn_FieldProblem(s.Reference,     'Reference',     'T', 35, 0))
    ) p(Problem)
    WHERE p.Problem IS NOT NULL;

    -- Expected segment count vs mapped rows.
    WITH Mapped AS
    (
        SELECT MessageId FROM #Header
        UNION ALL SELECT MessageId FROM #Customer
        UNION ALL SELECT MessageId FROM #CustomerAddress
        UNION ALL SELECT MessageId FROM #Account
        UNION ALL SELECT MessageId FROM #Premise
        UNION ALL SELECT MessageId FROM #Installation
        UNION ALL SELECT MessageId FROM #Meter
        UNION ALL SELECT MessageId FROM #MeterRegister
        UNION ALL SELECT MessageId FROM #Contract
        UNION ALL SELECT MessageId FROM #Agreement
        UNION ALL SELECT MessageId FROM #MoveInOut
        UNION ALL SELECT MessageId FROM #Payment
    ),
    MappedCount AS
    (
        SELECT MessageId, COUNT(*) AS RowCnt FROM Mapped GROUP BY MessageId
    )
    INSERT INTO #Err (MessageId, ErrorText)
    SELECT m.MessageId,
           CONCAT(N'Found ', m.ExpectedRowCount, N' segments but mapped ', ISNULL(mc.RowCnt, 0),
                  N'; a segment is in an unexpected place.')
    FROM #Msg m
    LEFT JOIN MappedCount mc ON mc.MessageId = m.MessageId
    WHERE m.Doc IS NOT NULL AND m.ExpectedRowCount <> ISNULL(mc.RowCnt, 0);

    UPDATE m
    SET IsPassed = 1
    FROM #Msg m
    WHERE NOT EXISTS (SELECT 1 FROM #Err e WHERE e.MessageId = m.MessageId);

    --------------------------------------------------------------------------------------------
    -- 4. Typed rows into staging, for passed messages only.
    --    TRY_CONVERT, not CONVERT: SQL Server may evaluate the conversion before the join filter,
    --    i.e. also for rows of failed messages. For passed rows the values are already validated,
    --    so TRY_CONVERT returns exactly the same result.
    --------------------------------------------------------------------------------------------
    INSERT INTO stg.MessageHeader (MessageId, BatchId, MessageType, CreatedAt, Sender)
    SELECT s.MessageId, @BatchId, s.MessageType, TRY_CONVERT(datetime2(0), s.CreatedAt, 126), s.Sender
    FROM #Header s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Customer (MessageId, BatchId, CustomerId, Category, FirstName, LastName, OrgName, Email, Phone, ValidFrom)
    SELECT s.MessageId, @BatchId, s.CustomerId, s.Category, s.FirstName, s.LastName, s.OrgName, s.Email, s.Phone,
           TRY_CONVERT(date, s.ValidFrom, 23)
    FROM #Customer s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.CustomerAddress (MessageId, BatchId, CustomerId, AddressType, Street, HouseNo, PostalCode, City, Country)
    SELECT s.MessageId, @BatchId, s.CustomerId, s.AddressType, s.Street, s.HouseNo, s.PostalCode, s.City, s.Country
    FROM #CustomerAddress s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Account (MessageId, BatchId, AccountId, CustomerId, AccountClass, BillingCycle, PaymentMethod, CreditLimit, OpenedOn)
    SELECT s.MessageId, @BatchId, s.AccountId, s.CustomerId, s.AccountClass, s.BillingCycle, s.PaymentMethod,
           TRY_CONVERT(decimal(18,2), s.CreditLimit), TRY_CONVERT(date, s.OpenedOn, 23)
    FROM #Account s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Premise (MessageId, BatchId, PremiseId, ConnectionObjectId, PremiseType, Street, HouseNo, Unit, PostalCode, City, Country)
    SELECT s.MessageId, @BatchId, s.PremiseId, s.ConnectionObjectId, s.PremiseType, s.Street, s.HouseNo, s.Unit,
           s.PostalCode, s.City, s.Country
    FROM #Premise s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Installation (MessageId, BatchId, InstallationId, PremiseId, Division, RateCategory, ValidFrom)
    SELECT s.MessageId, @BatchId, s.InstallationId, s.PremiseId, s.Division, s.RateCategory, TRY_CONVERT(date, s.ValidFrom, 23)
    FROM #Installation s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Meter (MessageId, BatchId, MeterId, InstallationId, SerialNumber, MeterType, Manufacturer, InstallDate, RemoveDate)
    SELECT s.MessageId, @BatchId, s.MeterId, s.InstallationId, s.SerialNumber, s.MeterType, s.Manufacturer,
           TRY_CONVERT(date, s.InstallDate, 23), TRY_CONVERT(date, s.RemoveDate, 23)
    FROM #Meter s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.MeterRegister (MessageId, BatchId, MeterId, RegisterNo, RegisterType, UnitOfMeasure, Decimals)
    SELECT s.MessageId, @BatchId, s.MeterId, s.RegisterNo, s.RegisterType, s.UnitOfMeasure, TRY_CONVERT(int, s.Decimals)
    FROM #MeterRegister s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Contract (MessageId, BatchId, ContractId, AccountId, InstallationId, StartDate, EndDate)
    SELECT s.MessageId, @BatchId, s.ContractId, s.AccountId, s.InstallationId,
           TRY_CONVERT(date, s.StartDate, 23), TRY_CONVERT(date, s.EndDate, 23)
    FROM #Contract s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Agreement (MessageId, BatchId, AgreementId, ContractId, AgreementType, PriceLevel, DiscountPct, ValidFrom, ValidTo)
    SELECT s.MessageId, @BatchId, s.AgreementId, s.ContractId, s.AgreementType, s.PriceLevel,
           TRY_CONVERT(decimal(5,2), s.DiscountPct), TRY_CONVERT(date, s.ValidFrom, 23), TRY_CONVERT(date, s.ValidTo, 23)
    FROM #Agreement s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.MoveInOut (MessageId, BatchId, EventId, EventType, CustomerId, AccountId, PremiseId, EventDate)
    SELECT s.MessageId, @BatchId, s.EventId, s.EventType, s.CustomerId, s.AccountId, s.PremiseId, TRY_CONVERT(date, s.EventDate, 23)
    FROM #MoveInOut s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    INSERT INTO stg.Payment (MessageId, BatchId, PaymentId, AccountId, Amount, Currency, PaymentDate, PaymentMethod, Reference)
    SELECT s.MessageId, @BatchId, s.PaymentId, s.AccountId, TRY_CONVERT(decimal(18,2), s.Amount), s.Currency,
           TRY_CONVERT(date, s.PaymentDate, 23), s.PaymentMethod, s.Reference
    FROM #Payment s JOIN #Msg m ON m.MessageId = s.MessageId AND m.IsPassed = 1;

    --------------------------------------------------------------------------------------------
    -- 5. One evidence row per message, then the shared finalize step.
    --------------------------------------------------------------------------------------------
    INSERT INTO dbo.LoadEvidence_Message
        (MessageId, ArrivalNo, BatchId, MessageType, ReceivedAt, RawXmlHash, ExpectedRowCount,
         ValidationStatus, ErrorDetail, Approach)
    SELECT m.MessageId,
           1,
           @BatchId,
           (SELECT TOP (1) LEFT(h.MessageType, 30) FROM #Header h WHERE h.MessageId = m.MessageId),
           m.ReceivedAt,
           m.RawXmlHash,
           m.ExpectedRowCount,
           CASE WHEN m.IsPassed = 1 THEN 'Passed' ELSE 'Failed' END,
           e.ErrorDetail,
           'A'
    FROM #Msg m
    OUTER APPLY
    (
        SELECT LEFT(STRING_AGG(CAST(x.ErrorText AS nvarchar(max)), N'; ') WITHIN GROUP (ORDER BY x.ErrorSeq), 4000)
               AS ErrorDetail
        FROM #Err x
        WHERE x.MessageId = m.MessageId
    ) e;

    EXEC dbo.usp_FinalizeBatch @BatchId = @BatchId, @Approach = 'A';

    COMMIT TRANSACTION;
END;
GO
