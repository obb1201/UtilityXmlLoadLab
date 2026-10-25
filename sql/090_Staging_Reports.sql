-- 090: Reports for reading the evidence. Safe to run more than once.
USE SapStaging;
GO

------------------------------------------------------------------------------------------------
-- dbo.vw_BatchReconciliation: staged vs promoted totals per batch.
-- For a promoted batch: StagedRowTotal = HeaderRows + PromotedRows + SupersededRows + ExcludedRows.
------------------------------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vw_BatchReconciliation
AS
SELECT b.BatchId,
       b.Status,
       b.Approach,
       b.MessageCount,
       b.PassedCount,
       b.FailedCount,
       b.DuplicateCount,
       b.ErrorFindingCount,
       b.WarningFindingCount,
       b.StagedRowTotal,
       h.HeaderRows,
       p.PromotedRows,
       p.SupersededRows,
       p.ExcludedRows,
       b.PromotedRowTotal,
       CASE
           WHEN b.Status <> 'Promoted' THEN NULL
           WHEN b.StagedRowTotal = h.HeaderRows + p.PromotedRows + p.SupersededRows + p.ExcludedRows THEN 'OK'
           ELSE 'MISMATCH'
       END AS ReconciliationResult
FROM dbo.LoadEvidence_Batch b
OUTER APPLY (SELECT COUNT(*) AS HeaderRows FROM stg.MessageHeader x WHERE x.BatchId = b.BatchId) h
OUTER APPLY
(
    SELECT SUM(x.PromotedRows) AS PromotedRows, SUM(x.SupersededRows) AS SupersededRows, SUM(x.ExcludedRows) AS ExcludedRows
    FROM dbo.LoadEvidence_Promotion x
    WHERE x.BatchId = b.BatchId
) p;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ReportCompleteness: one result set of checks that together show nothing was lost.
-- Every line should read OK. If a TestManifest exists (generated data), it also proves that each
-- injected fault was detected and each clean message passed.
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ReportCompleteness
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Check TABLE (CheckNo int IDENTITY(1, 1), CheckName nvarchar(160), Expected bigint, Actual bigint, IsInfo bit DEFAULT (0));

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Inbox messages that are in a batch', (SELECT COUNT_BIG(*) FROM dbo.Inbox),
           (SELECT COUNT_BIG(*) FROM dbo.Inbox WHERE BatchId IS NOT NULL);

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Inbox messages with evidence', (SELECT COUNT_BIG(*) FROM dbo.Inbox),
           (SELECT COUNT_BIG(*) FROM dbo.Inbox i
            WHERE EXISTS (SELECT 1 FROM dbo.LoadEvidence_Message e WHERE e.MessageId = i.MessageId AND e.ArrivalNo = 1));

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Duplicate arrivals with evidence', (SELECT COUNT_BIG(*) FROM dbo.InboxDuplicate),
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ArrivalNo > 1);

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Evidence hash matches the Inbox raw XML', (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ArrivalNo = 1),
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message e JOIN dbo.Inbox i ON i.MessageId = e.MessageId
            WHERE e.ArrivalNo = 1 AND e.RawXmlHash = HASHBYTES('SHA2_256', i.RawXml));

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Passed messages: staged rows = expected rows',
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ValidationStatus = 'Passed'),
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ValidationStatus = 'Passed' AND StagedRowCount = ExpectedRowCount);

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Failed messages: every one has an error detail',
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ValidationStatus = 'Failed'),
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ValidationStatus = 'Failed' AND ErrorDetail IS NOT NULL);

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Batches with a batch evidence row', (SELECT COUNT_BIG(DISTINCT BatchId) FROM dbo.Inbox WHERE BatchId IS NOT NULL),
           (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Batch);

    INSERT INTO @Check (CheckName, Expected, Actual)
    SELECT N'Promoted batches that reconcile', (SELECT COUNT_BIG(*) FROM dbo.vw_BatchReconciliation WHERE Status = 'Promoted'),
           (SELECT COUNT_BIG(*) FROM dbo.vw_BatchReconciliation WHERE Status = 'Promoted' AND ReconciliationResult = 'OK');

    IF EXISTS (SELECT 1 FROM dbo.TestManifest)
    BEGIN
        INSERT INTO @Check (CheckName, Expected, Actual)
        SELECT N'Manifest: clean messages that Passed',
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest WHERE ArrivalNo = 1 AND FaultKind IN ('None', 'OverlapContract', 'MeterDoubleInstall')),
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest t JOIN dbo.LoadEvidence_Message e
                    ON e.MessageId = t.MessageId AND e.ArrivalNo = t.ArrivalNo
                WHERE t.ArrivalNo = 1 AND t.FaultKind IN ('None', 'OverlapContract', 'MeterDoubleInstall')
                  AND e.ValidationStatus = 'Passed');

        INSERT INTO @Check (CheckName, Expected, Actual)
        SELECT N'Manifest: injected faults that Failed',
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest WHERE ArrivalNo = 1 AND FaultKind IN ('Malformed', 'MissingRequired', 'BadValue')),
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest t JOIN dbo.LoadEvidence_Message e
                    ON e.MessageId = t.MessageId AND e.ArrivalNo = t.ArrivalNo
                WHERE t.ArrivalNo = 1 AND t.FaultKind IN ('Malformed', 'MissingRequired', 'BadValue')
                  AND e.ValidationStatus = 'Failed');

        INSERT INTO @Check (CheckName, Expected, Actual)
        SELECT N'Manifest: duplicate arrivals recorded as Duplicate',
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest WHERE ArrivalNo > 1),
               (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ArrivalNo > 1 AND ValidationStatus = 'Duplicate');

        INSERT INTO @Check (CheckName, Expected, Actual)
        SELECT N'Manifest: passed messages with expected rows as generated',
               (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message WHERE ArrivalNo = 1 AND ValidationStatus = 'Passed'),
               (SELECT COUNT_BIG(*) FROM dbo.LoadEvidence_Message e JOIN dbo.TestManifest t
                    ON t.MessageId = e.MessageId AND t.ArrivalNo = 1
                WHERE e.ArrivalNo = 1 AND e.ValidationStatus = 'Passed' AND e.StagedRowCount = t.ExpectedRowCount);

        -- INFO only: when a technical fault hits the partner data (for example the message with the
        -- other contract failed), there is nothing valid to overlap with. Run the generator with
        -- --fault-rate 0 to see every business-rule fault caught.
        INSERT INTO @Check (CheckName, Expected, Actual, IsInfo)
        SELECT N'Manifest: business-rule faults with a finding (INFO: partner data may have failed)',
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest WHERE FaultKind IN ('OverlapContract', 'MeterDoubleInstall')),
               (SELECT COUNT_BIG(*) FROM dbo.TestManifest t
                WHERE t.FaultKind IN ('OverlapContract', 'MeterDoubleInstall')
                  AND EXISTS (SELECT 1 FROM dbo.LoadEvidence_Finding f WHERE f.MessageId = t.MessageId)),
               1;
    END;

    SELECT CheckNo, CheckName, Expected, Actual,
           CASE WHEN Expected = Actual THEN 'OK' WHEN IsInfo = 1 THEN 'INFO' ELSE 'CHECK' END AS Result
    FROM @Check
    ORDER BY CheckNo;
END;
GO

------------------------------------------------------------------------------------------------
-- dbo.usp_ReportSummary: totals for a quick look (messages, rows, findings, batch status).
------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ReportSummary
AS
BEGIN
    SET NOCOUNT ON;

    SELECT ValidationStatus, COUNT(*) AS Arrivals, SUM(StagedRowCount) AS StagedRows
    FROM dbo.LoadEvidence_Message
    GROUP BY ValidationStatus
    ORDER BY ValidationStatus;

    SELECT Status, COUNT(*) AS Batches, SUM(MessageCount) AS Messages, SUM(StagedRowTotal) AS StagedRows,
           SUM(PromotedRowTotal) AS PromotedRows
    FROM dbo.LoadEvidence_Batch
    GROUP BY Status
    ORDER BY Status;

    SELECT CheckName, Severity, EntityType, COUNT(*) AS Findings
    FROM dbo.LoadEvidence_Finding
    GROUP BY CheckName, Severity, EntityType
    ORDER BY Severity, CheckName, EntityType;
END;
GO
