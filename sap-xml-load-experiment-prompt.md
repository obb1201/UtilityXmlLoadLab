# Prompt: SAP XML One-Time Load — Local Experiment

Paste everything below into Claude Code (run `claude` in an empty folder).

---

Build a local experiment that compares two ways of loading SAP XML messages into SQL Server: **T-SQL shredding** vs **.NET parsing + SqlBulkCopy**. It is a one-time load of ~1 million messages, with an evidence/monitoring step before data is promoted to the final target database.

## The problem this solves
The real goal is not "SQL vs .NET." It is: **move ~1 million SAP records into a new relational system once, and prove the data arrived complete and correct before anyone relies on it.** Every design choice should serve these concerns:

- **Translation:** SAP sends hierarchical XML; the target is flat relational tables. The XML-to-table mapping must be clear and easy to change when the real XML turns out messier than the spec.
- **Trust and audit:** this is a one-time load, so there is no "fix it next run." We must be able to show that every message was received, nothing was silently dropped, and every failure was caught and explained before data reaches the final database. The evidence tables and approval gate exist for this.
- **Risk control:** staging is kept separate from the target so a bad batch can be stopped, fixed, and rerun without touching final data.
- **Time window:** the full load must finish within a cutover/maintenance window, so parsing and loading speed matter.
- **Decide with evidence:** the architecture choice (T-SQL vs .NET parsing) should come from measured benchmark results on realistic data, not opinion.

When trade-offs come up while building, favour completeness and auditability first, then speed.

## Environment
- SQL Server 2022 Developer in Docker (`mcr.microsoft.com/mssql/server:2022-latest`), with a `docker-compose.yml`.
- .NET 8 (or newer LTS). Use **CoreWCF** for the receiving endpoint, since classic WCF hosting isn't available on modern .NET.
- One solution, folder per project. Include a README with run steps.

## Databases
- `SapStaging`: inbox, staging, and evidence tables.
- `SapTarget`: final tables.

## Components

### 1. Mock SAP message generator (console app)
- Generates realistic SAP-style XML (IDoc-like: header + nested repeating segments, 2–3 levels deep). Each message has a unique `MessageId` and a message type.
- Model the messages on a **generic utility (SAP IS-U style) domain**, using fully synthetic data:
  - **Customer** (business partner): name, addresses, contact details.
  - **Account** (contract account): billing and payment settings; belongs to a customer.
  - **Premise**: service address; part of a connection object (building).
  - **Installation**: the service point at a premise, by utility division (e.g., electricity).
  - **Meter** (device): serial number, type, registers; installed in an installation, with install/remove dates.
  - **Contract**: links an account to an installation, with start/end dates.
  - **Agreement**: rate or service terms attached to a contract.
  - **Move-in / Move-out**: an event that starts or ends contracts for a customer at a premise.
  - **Payment**: amount, date, and method, applied to an account.
- Generate data with **consistent references** (e.g., a contract points to an existing account and installation) and realistic volume ratios (many payments per account; one premise has a few installations; meters get replaced over time).
- Include realistic edge cases: a move-out before a move-in for the same premise, meter swaps, contracts with open end dates, and references to entities that arrive in a later message.
- Configurable count: 10k by default, up to 1M.
- Options to inject faults: malformed XML, missing required elements, bad dates/decimals, duplicate MessageIds (~1% each).
- Can send messages to the CoreWCF endpoint, or write them straight to the inbox table for fast tests.

### 2. CoreWCF receiver
- One operation: accept the XML string and insert the raw XML + MessageId + ReceivedAt into `dbo.Inbox`, then return. No parsing in the request path.
- Duplicate MessageId → skip and return success (idempotent).

### 3. Inbox table
- `dbo.Inbox(MessageId PK, RawXml xml, ReceivedAt, BatchId NULL, Status)`.

### 4. Staging tables
- One staging table per entity (Customer, Account, Premise, Installation, Meter, Contract, Agreement, MoveInOut, Payment, plus child tables for repeating segments such as addresses and meter registers). Every row carries `MessageId` and `BatchId`.
- No foreign keys in staging, so messages can land in any order. Keep indexes minimal for loading.

### 5. Parsing approach A — T-SQL
- Stored procedure `usp_ShredBatch @BatchId` that shreds one batch from the Inbox using `xml.nodes()` with nested `CROSS APPLY`. No parent axis (`..`). Use `.value('(X/text())[1]', ...)`.
- A SQL Agent-style driver script that processes the batches in a loop, one transaction per batch.

### 6. Parsing approach B — .NET
- Console app: read batches from the Inbox, parse with `XmlReader` (streaming), map to `DataTable`s, and write with `SqlBulkCopy`.
- Configurable degree of parallelism.
- Writes to the **same** staging tables as approach A.

### 7. Evidence tables
- `dbo.LoadEvidence_Message`: MessageId, BatchId, ReceivedAt, RawXmlHash (SHA2_256), ExpectedRowCount, StagedRowCount, ValidationStatus (Passed/Failed/Duplicate), ErrorDetail, ProcessedAt.
- `dbo.LoadEvidence_Batch`: BatchId, MessageCount, PassedCount, FailedCount, StagedRowTotal, PromotedRowTotal, Status (Staged/Approved/Promoted/Rejected), ApprovedBy, ApprovedAt.
- Both approaches fill these tables. Checks: well-formed XML, required elements present, expected rows = staged rows, type conversion OK, duplicate detection.
- Add **referential checks across entities** before promotion: orphans (e.g., a contract whose account or installation doesn't exist, a payment for an unknown account), meters installed in more than one installation at the same time, and overlapping contracts for the same installation. Record each finding with its MessageId so it can be traced.

### 8. Promotion gate
- `usp_ApproveBatch @BatchId, @ApprovedBy`, plus an auto-approve option when FailedCount = 0.
- `usp_PromoteBatch @BatchId`: set-based MERGE from staging into `SapTarget`, only for Approved batches. Fill PromotedRowTotal and mark the batch Promoted.
- Promote in **dependency order** so the target's foreign keys hold: Customer → Account → Premise → Installation → Meter → Contract → Agreement → MoveInOut → Payment.
- `SapTarget` has proper primary keys and foreign keys between these entities.
- A reconciliation query: staged vs promoted totals per batch.

### 9. Benchmark harness
- A script or console command that resets the staging tables, runs A and B over the same inbox data (10k, 100k, and optionally 1M), and reports for each: elapsed time, messages/sec, rows/sec, and peak SQL Server CPU if feasible.
- Prints a comparison table and projects the time for 1M messages.

## Data privacy (strict)
- Use **synthetic data only**. Generate fake names, addresses, IDs, serial numbers, and amounts (e.g., with Bogus). No real customer, account, or company data anywhere: code, scripts, test files, or logs.
- Field and table names follow generic, publicly documented utility/SAP IS-U concepts only. Do not guess or reproduce any company-specific schema, codes, or rates.
- If I later supply real XML samples to refine the mapping, they must be anonymized first, kept out of source control (add the folder to `.gitignore`), and never quoted in output.

## Conventions
- C# in a layered structure (Core / DataAccess / Business / console or service hosts).
- Follow my team's C# coding standard if a skill or style file for it is available.
- Use parameterized SQL only. Put the connection string in `appsettings.json` and user secrets; nothing hard-coded.

## Deliverables
1. Working solution + `docker-compose.yml` + SQL scripts (numbered, idempotent).
2. README: setup, how to generate data, how to run each approach, how to approve/promote, and how to read the evidence and benchmark output.
3. Run the 10k benchmark yourself and report the results.
