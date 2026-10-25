# Change log

Notable changes to this project. Newest entries first.

## 2026-10-25 (documentation)

- Added [DEADLOCK-ANALYSIS.md](DEADLOCK-ANALYSIS.md): the three causes of the parallel-load
  deadlocks, how they were fixed, the rules for the real load, and how to investigate a new deadlock.
  Linked from the README and the benchmark results.

## 2026-10-25 (first working version)

Decisions confirmed with the user: target `net10.0`; batches of 1,000 messages; mixed message types;
build straight through to a working 10k benchmark.

### Added
- **Environment:** `docker-compose.yml` with SQL Server 2022 Developer on port 14333; the password comes
  from `.env` (git-ignored, `.env.example` provided). `.gitignore` covers the `samples-real/` folder and
  `benchmark-results/`.
- **Solution:** one project per folder:
  - Core, DataAccess and Business layers;
  - Generator, Receiver (CoreWCF), Loader and Benchmark hosts;
  - a Tests project.
  Connection strings are in `appsettings.json`; the password is in shared user secrets.
- **SQL scripts `sql/001`–`090`, numbered and idempotent:**
  - databases, Inbox and duplicates, staging, evidence and target schema;
  - field validation, the shared batch begin/finalize procedures;
  - approach A (`usp_ShredBatch` plus the driver procedure);
  - reference checks, approve/reject/promote, and the reports.
- **Generator:** synthetic IS-U style messages made with Bogus, in 6 mixed message types. It covers the
  edge cases from the spec and injects faults. A test manifest records every injected fault.
- **Approach B:** streaming `XmlReader` parser with a declarative mapping (`StagingMap.cs`), the same
  field rules as A, and `SqlBulkCopy` with configurable parallelism.
- **Benchmark harness:** measures time, msgs/s, rows/s, SQL container CPU and client CPU. It
  fingerprints the output of every run, so A and B are proven to write identical data.
- 28 unit tests.
- [BENCHMARK-RESULTS.md](BENCHMARK-RESULTS.md) with the 10k and 100k results and a recommendation.

### Design decisions (deviations from the spec)
- **Raw XML as text:** `Inbox.RawXml` is `nvarchar(max)`, not `xml`, so malformed messages are stored
  and get evidence instead of being refused at the door.
- **Duplicate arrivals are recorded:** they go to `dbo.InboxDuplicate`. `LoadEvidence_Message` has one
  row per arrival, keyed by MessageId + ArrivalNo. A duplicate with different content counts as Failed.
- **Extra evidence tables:** `LoadEvidence_Finding` (referential and business findings, Error or
  Warning) and `LoadEvidence_Promotion` (promoted / superseded / excluded rows per batch and table).
- **Approval:** auto-approve also requires no findings at all, not only `FailedCount = 0`.
- **Promotion in order, across batches:** batches are promoted in batch order. `usp_PromoteBatches`
  promotes a range, because references can point into a later batch (`DependsOnBatchId`).
- **Segment rule:** a segment is mapped only for the message types that carry it, in both approaches.
  Anything else makes the message fail, so it is never dropped silently.

### Fixed during testing
- **Approach A, CONVERT order:** `CONVERT` in the typed inserts could run before the join filter and hit
  rows of failed messages (error 241). It now uses `TRY_CONVERT` on values that are already validated.
- **Approach A, parallel plans:** parallel plans for `xml.nodes()` were about 15 times slower. The XML
  statements now use `OPTION (MAXDOP 1)`. 10k went from 88 s to about 11 s serially.
- **Parallel batches blocked each other:** a key-range lock in `usp_BeginBatch` made them wait. It was
  replaced by a per-batch `sp_getapplock`.
- **Parallel batches deadlocked in `usp_FinalizeBatch`:**
  - page locks are now off on the shared tables;
  - evidence and Inbox rows are reached only through the BatchId indexes (index hints);
  - per-batch statements use MAXDOP 1.
- **Benchmark comparison:** the fingerprint compared rows by position, but the query had no
  `ORDER BY`. It now orders the rows and compares them by table name.

## 2026-10-25

- Added `CLAUDE.md` with project guidance: scope, planned architecture, constraints, and data privacy rules.
- Added working rules to `CLAUDE.md`: clear, simple English; ask when requirements are unclear; record notable changes in this change log.
- Created this change log.
