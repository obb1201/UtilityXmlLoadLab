# UtilityXmlLoadLab

A local experiment for a **one-time load of ~1 million SAP IS-U style XML messages** into SQL Server.
It compares two ways to turn the XML into rows, and proves the data arrived complete and correct
before anything reaches the final database:

- **A: T-SQL shredding.** `usp_ShredBatch` uses `xml.nodes()` with nested `CROSS APPLY`.
- **B: .NET parsing.** `XmlReader` (streaming), then `DataTable`, then `SqlBulkCopy`, with configurable parallelism.

Both approaches write to the **same** staging and evidence tables, through the same shared
begin/finalize procedures. The benchmark checks that they produce identical output.

All data is synthetic (generated with Bogus). No real customer or company data is used anywhere.

Results: see [docs/BENCHMARK-RESULTS.md](docs/BENCHMARK-RESULTS.md).

## How it fits together

```
Generator ──SOAP──► CoreWCF Receiver ──► SapStaging.dbo.Inbox (raw XML, no parsing)
    └────────── direct bulk insert (fast tests) ──┘        │
                                             usp_CreateBatches (1,000 per batch)
                                                           │
             A: usp_ShredBatch (T-SQL)   or   B: XmlReader + SqlBulkCopy (.NET)
                                                           │
                 staging tables (stg.*)  +  LoadEvidence_Message / LoadEvidence_Batch
                                                           │
            usp_CheckReferences ──► usp_ApproveBatch ──► usp_PromoteBatches (MERGE)
                                                           │
                                         SapTarget (full primary and foreign keys)
```

| Folder | What it is |
|---|---|
| `src/UtilityXmlLoadLab.Core` | Mapping (`StagingMap.cs`: XML paths to staging columns), message models |
| `src/UtilityXmlLoadLab.DataAccess` | SQL access: Inbox, bulk writer, gate, benchmark, script deployer |
| `src/UtilityXmlLoadLab.Business` | .NET parser and validator, batch runner, generator, fault injector, benchmark |
| `src/UtilityXmlLoadLab.Generator` | Console: generate messages and deliver them |
| `src/UtilityXmlLoadLab.Receiver` | CoreWCF service: `SubmitMessage(messageId, rawXml)` |
| `src/UtilityXmlLoadLab.Loader` | Console: deploy DB, batches, load A/B, check, approve, promote, reports |
| `src/UtilityXmlLoadLab.Benchmark` | Console: run A and B over the same data and compare |
| `tests/UtilityXmlLoadLab.Tests` | Unit tests (field rules, parser, generator) |
| `sql/` | Numbered, idempotent scripts (run in name order) |
| `samples-real/` | Git-ignored folder for anonymized real samples, if ever needed |

## Setup

Requirements: Docker Desktop and the .NET 10 SDK. The SDK that ships with .NET 11 also works; the projects target `net10.0`.

1. **Start SQL Server 2022.**
   ```powershell
   Copy-Item .env.example .env        # then edit .env and choose a strong password
   docker compose up -d               # SQL Server on localhost,14333
   ```
2. **Store the same password for the .NET apps** (user secrets; set once, shared by all apps):
   ```powershell
   dotnet user-secrets set Sql:Password "<your password>" --project src/UtilityXmlLoadLab.Loader
   ```
   You can also set the environment variable `UXL_Sql__Password` instead. The connection string
   (without the password) is in each app's `appsettings.json`.
3. **Build and create the databases** (runs every `sql/*.sql` script in order; safe to repeat):
   ```powershell
   dotnet build
   dotnet run --project src/UtilityXmlLoadLab.Loader -- deploy-db
   ```

The shorthand `loader`, `generator` and `benchmark` below means
`dotnet run --project src/UtilityXmlLoadLab.<Name> --`.

## Generate data

```powershell
generator --count 10000 --reset                 # direct insert into the Inbox (fast)
```

- **Message mix:** 6 mixed message types: `CUSTOMER_MASTER` (customer, addresses, account),
  `PREMISE_MASTER` (premise, installations, meters, registers), `MOVE_IN` / `MOVE_OUT` (move event,
  contracts, agreements), `METER_SWAP` (removed and installed meter), and `PAYMENT_BATCH`.
- **Realism:** references are consistent and ratios are realistic. Messages arrive slightly out of
  order, so references often point to a later message. Some move-outs arrive before their move-in.
  Meters are swapped and contracts can have open end dates.
- **Faults:** `--fault-rate 0.01` (default) injects about 1% each of malformed XML, a missing
  required element, a bad date or decimal, and a duplicate MessageId.
  `--business-fault-rate 0.002` adds overlapping contracts and meters installed twice.
- **Test manifest:** every injected fault is recorded in `dbo.TestManifest`, so the completeness
  report can prove each one was caught.
- **Batches:** the generator puts the messages into batches of 1,000 at the end
  (`--batch-size`, or `--no-batches`).
- **Same seed, same data:** `--seed` (default 42) makes runs repeatable.

To go through the **CoreWCF receiver** instead:

```powershell
dotnet run --project src/UtilityXmlLoadLab.Receiver     # http://localhost:5080/InboxService.svc (?wsdl)
generator --count 10000 --reset --target wcf --send-parallel 8
```

The receiver does no parsing. It stores MessageId, raw XML and ReceivedAt. A duplicate MessageId is
recorded in `dbo.InboxDuplicate` and still returns success (`Duplicate`).

## Run each approach

```powershell
loader load --approach A            # T-SQL. dop 1 = the SQL Agent-style driver procedure
loader load --approach A --dop 8    # T-SQL, 8 batches side by side (separate sessions)
loader load --approach B --dop 8    # .NET XmlReader + SqlBulkCopy, 8 batches side by side
loader reset-staging                # empty staging + evidence (Inbox and batches stay)
```

- **Batches and failures:** each batch is one transaction. A batch that fails is rolled back, logged
  in `dbo.LoadRunBatch`, and reported; the run goes on. By default only batches not yet staged are
  loaded. `--reload` also reloads batches that are Staged.
- **Approach A from SQL:** you can also run A directly (for example as a SQL Agent job step):
  `EXEC SapStaging.dbo.usp_RunShredDriver;`

## Check, approve and promote

```powershell
loader check                                     # cross-entity checks over all staged batches
loader approve --auto                            # approves only perfectly clean batches
loader approve --batch 3 --by "your name"        # manual approval (failures/findings allowed, recorded)
loader reject  --batch 3 --by "your name" --reason "..."
loader promote --all                             # or --batch 3, or --from 2 --to 8
```

Rules enforced by the procedures:

- **Approval:**
  - Approval needs a current reference check. Staging or rejecting a batch afterwards makes the
    check out of date.
  - Auto-approve requires `FailedCount = 0` and no findings at all.
  - Manual approval records the counts in `ApprovalNote`.
- **Promotion:**
  - Only Approved batches are promoted, **in batch order**.
  - A batch that references an entity found only in a later batch has `DependsOnBatchId` set.
    Promote a range that includes that later batch (`--from/--to` or `--all`).
  - Promotion runs in dependency order (Customer → Account → Premise → Installation → Meter →
    Contract → Agreement → MoveInOut → Payment) as set-based `MERGE`.
  - The latest version of an entity wins (by message `CreatedAt`), and an older message never
    overwrites newer target data.
  - Rows from failed messages and rows with an **Error** finding are never promoted.

## Read the evidence

```powershell
loader report                    # summary + completeness + reconciliation
loader report completeness       # every line should read OK
```

| Table / view | What it proves |
|---|---|
| `dbo.LoadEvidence_Message` | One row per arrival: SHA2_256 hash of the raw XML, expected vs staged rows, Passed / Failed / Duplicate, error detail |
| `dbo.LoadEvidence_Batch` | Per batch: counts, findings, status (Staged / Approved / Promoted / Rejected), who approved and when |
| `dbo.LoadEvidence_Finding` | Each referential or business finding, with the MessageId that carried the row |
| `dbo.LoadEvidence_Promotion` | Per batch and table: staged = promoted + superseded + excluded |
| `dbo.vw_BatchReconciliation` | Staged vs promoted per batch, with an OK / MISMATCH result |
| `dbo.LoadRun`, `dbo.LoadRunBatch` | Every load run and batch, with timing and errors |

How "nothing was lost" is shown:

- **Every message is accounted for:**
  - Every Inbox message gets exactly one evidence row. `usp_FinalizeBatch` throws if not.
  - Every duplicate arrival is recorded.
  - The stored hash is re-checked against the Inbox.
- **Every segment is accounted for:** the expected row count of a message is every segment element
  anywhere in the XML, counted separately from the mapping. A segment in an unexpected place, or in
  a message type that does not carry it, makes the message fail instead of disappearing.
- **All-or-nothing per message:** a message with any problem stages no rows.

Example queries:

```sql
SELECT * FROM SapStaging.dbo.LoadEvidence_Message WHERE ValidationStatus <> 'Passed';
SELECT * FROM SapStaging.dbo.LoadEvidence_Finding WHERE MessageId = '<id>';
SELECT * FROM SapStaging.dbo.vw_BatchReconciliation;
```

## Benchmark

```powershell
generator --count 10000 --reset
benchmark --dops 1,4,8                     # add --no-cpu to skip docker stats sampling
```

For each approach and DOP (degree of parallelism: how many batches run at once), the benchmark:

1. resets staging,
2. runs the load,
3. measures elapsed time, msgs/s, rows/s, peak and average SQL Server container CPU
   (`docker stats`), and client CPU,
4. fingerprints every staging and evidence table.

It then prints a comparison with a 1M projection and writes it to `benchmark-results/` (git-ignored).
It exits with code 1 if any run differs from the others or a batch failed. Staging is left with the
last run's output, so you can go on with `check`, `approve` and `promote`.

## Tests

```powershell
dotnet test
```

## Notes and known limits

- **Raw XML as text:** `Inbox.RawXml` is `nvarchar(max)`, not `xml`. The spec says `xml`, but a
  malformed message could then not be stored at all, and it would be lost without evidence.
- **No XML declaration:** messages must not have an `encoding="utf-8"` XML declaration.
  SQL Server cannot convert such an `nvarchar` to `xml`. Strip it at the receiver if real messages
  carry one.
- **Mapping in two places:** the XML-to-table mapping lives in `StagingMap.cs` (B) and
  `usp_ShredBatch` (A). Change both; the benchmark fingerprint shows any difference.
- **Strict value rules:** both approaches accept exactly the same values:
  - dates `yyyy-MM-dd`,
  - date-times `yyyy-MM-ddTHH:mm:ss`,
  - decimals with a dot and at most 2 decimal places.

  Anything else fails the message rather than being silently rounded or guessed.
- **Lock rules for parallel batches:** every per-batch statement reaches rows through its
  `BatchId` index, page locks are off on the shared tables, and per-batch statements use
  `MAXDOP 1`. Without this, parallel batches deadlocked. See
  [docs/DEADLOCK-ANALYSIS.md](docs/DEADLOCK-ANALYSIS.md).
