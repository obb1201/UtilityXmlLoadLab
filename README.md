# UtilityXmlLoadLab

**Load ~1 million SAP IS-U style XML messages into SQL Server once, and prove the data arrived
complete and correct before anyone relies on it.**

A working lab that compares two ways to load the messages (T-SQL shredding and .NET streaming plus
bulk copy), with an evidence trail, an approval gate, and measured results. It was built with an
AI coding agent (Claude Code) as an exercise in **agentic engineering**: clear specs, explicit
rules, and automatic checks that keep the agent's work honest.

All data is synthetic (generated with Bogus). No real customer or company data is used anywhere.

| | |
|---|---|
| **Stack** | .NET 10, SQL Server 2022 (Docker), CoreWCF, Microsoft.Data.SqlClient, Bogus, xUnit |
| **Result** | 1M messages staged in about **2 minutes** (projected from 100k), every message accounted for |
| **Recommendation** | .NET parsing (B): 2 to 3 times faster than T-SQL (A), and half the SQL Server CPU |

## 1. The problem

A one-time migration has no "fix it in the next run". The questions that matter are:

- **Translation:** SAP sends nested XML (header plus repeating segments, 2 to 3 levels deep). The
  target is flat relational tables with primary and foreign keys.
- **Trust:** can we show that every message was received, nothing was dropped silently, and every
  failure was caught and explained, before data reaches the final database?
- **Risk control:** can a bad batch be stopped, fixed and rerun without touching final data?
- **Time window:** does the load fit a cutover window?
- **Evidence over opinion:** should the XML be parsed in T-SQL or in .NET? Measure it, on the same
  data, with the same checks.

The priority is **completeness and auditability first, then speed**.

## 2. Demo

Five commands take you from an empty database to promoted, reconciled data
(see [Run it](#6-run-it) for setup):

```powershell
generator --count 10000 --reset    # 10k synthetic messages, ~1% each of 4 fault kinds injected
benchmark --dops 1,4,8             # runs A and B at 1, 4 and 8 batches in parallel, compares output
loader check                       # cross-message checks: orphans, double-installed meters, overlaps
loader approve --auto              # approves only batches with no failures and no findings
loader promote --all               # MERGE into the target database, in dependency order
loader report                      # summary, completeness, reconciliation
```

Benchmark result for 10k messages (recorded run; full report in
[docs/benchmarks/benchmark-10k.md](docs/benchmarks/benchmark-10k.md)):

| Approach | DOP | Elapsed | Msgs/s | Avg SQL CPU | Failed batches |
|---|---:|---:|---:|---:|---:|
| A: T-SQL shredding | 1 | 11.05 s | 905 | 96% | 0 |
| A: T-SQL shredding | 8 | 2.31 s | 4,328 | 388% | 0 |
| B: .NET XmlReader + SqlBulkCopy | 1 | 3.92 s | 2,552 | 41% | 0 |
| B: .NET XmlReader + SqlBulkCopy | 8 | 1.31 s | 7,642 | 304% | 0 |

DOP is the number of batches run at once. CPU is in % of one core.

What the demo proves:
- **Same output:** all runs write identical staging and evidence data, checked by row count and
  content checksum per table.
- **Every fault caught:** each of the 300 injected faults is recorded in a test manifest, and the
  completeness report shows it was caught.
- **Promotion reconciles:** for each batch and table, staged = promoted + superseded + excluded.

The 100k results, the 1M projection and the analysis are in
[docs/BENCHMARK-RESULTS.md](docs/BENCHMARK-RESULTS.md).

## 3. My contribution

I owned the problem, the rules and the validation. The code was written by Claude Code under them.

- **Framed the problem.** I wrote the [specification](sap-xml-load-experiment-prompt.md): the
  real goal (proof of completeness, not "SQL vs .NET"), the domain, the edge cases, the fault
  injection and the success criteria.
- **Set the rules.** [CLAUDE.md](CLAUDE.md) holds the constraints the agent must follow: the team
  C# standard, parameterized and idempotent SQL, strict data privacy, and the invariants that span
  components.
- **Made the decisions.** Target framework, batch size, message mix and scope, plus review of the
  design choices below.
- **Designed the validation.** The output is checked by machines, not by trust: fingerprint
  comparison of A and B, a fault manifest with known answers, a completeness report, and unit
  tests.
- **Drove the investigation.** Parallel batches deadlocked. We found three separate root causes,
  fixed them, and proved the fix with 20 clean parallel runs. Write-up:
  [docs/DEADLOCK-ANALYSIS.md](docs/DEADLOCK-ANALYSIS.md).

How I practise agentic engineering on this project (harness, context, skills, validation):
[docs/AGENTIC-ENGINEERING.md](docs/AGENTIC-ENGINEERING.md).

## 4. Architecture

```
Generator ──SOAP──► CoreWCF Receiver ──► SapStaging.dbo.Inbox (raw XML, no parsing)
    └────────── direct bulk insert (fast tests) ──┘        │
                                             usp_CreateBatches (1,000 per batch)
                                                           │
             A: usp_ShredBatch (T-SQL)   or   B: XmlReader + SqlBulkCopy (.NET)
                                                           │
                 staging tables (stg.*)  +  evidence tables (LoadEvidence_*)
                                                           │
            usp_CheckReferences ──► usp_ApproveBatch ──► usp_PromoteBatches (MERGE)
                                                           │
                                         SapTarget (full primary and foreign keys)
```

- **Receive:** the receiver stores MessageId, raw XML and arrival time, and returns. It does no
  parsing, so receipt never fails because of content. Duplicates are recorded, not lost.
- **Stage:** A and B parse a batch of 1,000 messages into the **same** staging tables. Shared
  begin and finalize procedures write the evidence, so both approaches are judged the same way.
- **Check:** cross-message checks find orphans, meters installed in two places at once, and
  overlapping contracts. Each finding points to the MessageId that carried the row.
- **Approve and promote:** only approved batches are promoted, in batch order and in dependency
  order (Customer → Account → Premise → Installation → Meter → Contract → Agreement → MoveInOut →
  Payment). Every row's outcome is recorded.

| Evidence | What it proves |
|---|---|
| `LoadEvidence_Message` | One row per arrival: SHA2_256 hash of the raw XML, expected vs staged rows, Passed / Failed / Duplicate, error detail |
| `LoadEvidence_Batch` | Per batch: counts, findings, status (Staged / Approved / Promoted / Rejected), who approved and when |
| `LoadEvidence_Finding` | Each referential or business finding, with its MessageId |
| `LoadEvidence_Promotion` | Per batch and table: staged = promoted + superseded + excluded |

| Folder | What it is |
|---|---|
| `src/UtilityXmlLoadLab.Core` | Mapping (`StagingMap.cs`: XML paths to staging columns), message models |
| `src/UtilityXmlLoadLab.DataAccess` | SQL access: Inbox, bulk writer, approval gate, benchmark, script deployer |
| `src/UtilityXmlLoadLab.Business` | Parser and validator, batch runner, generator, fault injector, benchmark |
| `src/UtilityXmlLoadLab.{Generator,Receiver,Loader,Benchmark}` | The four apps (console and CoreWCF service) |
| `tests/UtilityXmlLoadLab.Tests` | Unit tests (field rules, parser, generator) |
| `sql/` | Numbered, idempotent scripts (run in name order) |

## 5. Key decisions

| Decision | Why | Trade-off |
|---|---|---|
| Staging has no foreign keys; the target has full keys | Messages arrive in any order, and a reference can point to a later message | References are checked by a separate step, and promotion must follow dependency order |
| Raw XML stored as text (`nvarchar(max)`), not `xml` | A malformed message must still be stored and get evidence, instead of being refused at the door | Each approach converts to XML itself |
| All-or-nothing per message | A half-loaded message is worse than a failed one | One bad field fails the whole message |
| Expected row count is counted separately from the mapping | A segment the mapping misses makes the message fail instead of disappearing | The count logic must stay simple and independent |
| Both approaches share the evidence procedures | A fair comparison, and a fingerprint check that proves identical output | B must apply exactly the same field rules as A |
| Auto-approve only when there are no failures and no findings | Anything unusual needs a person, and their approval is recorded | More manual steps on dirty data |
| Latest version wins on promotion, by message creation time | Messages arrive out of order; an older message must never overwrite newer data | Needs a source timestamp on every target row |
| Parallel lock rules: BatchId indexes, no page locks, `MAXDOP 1` per batch | Parallel batches deadlocked without them | Rules must be followed in every new statement |
| **Recommend B at DOP 4 to 8, keep A as a cross-check** | B is 2 to 3 times faster and uses half the SQL Server CPU; A gives a cheap second opinion on the mapping | The mapping lives in two places |

More detail on each decision: [docs/CHANGELOG.md](docs/CHANGELOG.md).

## 6. Run it

Requirements: Docker Desktop and the .NET 10 SDK.

```powershell
Copy-Item .env.example .env              # then set a strong SA password in .env
docker compose up -d                     # SQL Server 2022 on localhost,14333
dotnet user-secrets set Sql:Password "<same password>" --project src/UtilityXmlLoadLab.Loader
dotnet build
dotnet run --project src/UtilityXmlLoadLab.Loader -- deploy-db    # creates both databases (safe to repeat)
dotnet test
```

The password can also come from the environment variable `UXL_Sql__Password`. All apps share one
user-secrets store.

Below, `loader`, `generator` and `benchmark` mean
`dotnet run --project src/UtilityXmlLoadLab.<Name> --`.

| Task | Command |
|---|---|
| Generate data (direct insert) | `generator --count 10000 --reset` (options: `--fault-rate`, `--business-fault-rate`, `--seed`, `--batch-size`) |
| Generate data through CoreWCF | start `dotnet run --project src/UtilityXmlLoadLab.Receiver`, then `generator --count 10000 --reset --target wcf --send-parallel 8` |
| Load with A or B | `loader load --approach A --dop 8` or `loader load --approach B --dop 8` |
| Run A from SQL (for example a SQL Agent job) | `EXEC SapStaging.dbo.usp_RunShredDriver;` |
| Check, approve, promote | `loader check`, `loader approve --auto`, `loader promote --all` |
| Manual approval or rejection | `loader approve --batch 3 --by "name"`, `loader reject --batch 3 --by "name" --reason "..."` |
| Reports | `loader report`, `loader report completeness` (every line should read OK) |
| Benchmark | `benchmark --dops 1,4,8` (writes a report to `benchmark-results/`) |
| Start again | `loader reset-staging` |

## 7. Limitations

- **Synthetic data.** Messages are about 2 KB. Real IDocs can be larger and deeper, which costs A
  more than B. Re-run the benchmark on anonymized real samples before deciding.
- **One machine.** The client and SQL Server share 16 CPUs. On separate servers, B would likely
  gain more.
- **Projections.** The 1M numbers are scaled up from 100k. The reference check and promotion work
  across all batches and should be measured at 1M (`generator --count 1000000` is supported).
- **Mapping in two places.** `StagingMap.cs` (B) and `usp_ShredBatch` (A) must be changed together.
  The benchmark fingerprint shows any difference.
- **No XML declaration.** SQL Server cannot convert an `nvarchar` that has an
  `encoding="utf-8"` declaration to `xml`. Real messages would need it stripped at the receiver.
- **Strict value rules.** Only `yyyy-MM-dd` dates, `yyyy-MM-ddTHH:mm:ss` date-times and decimals
  with a dot and at most 2 places are accepted. Anything else fails the message.
- **Lab only.** The receiver has no authentication or TLS. It is not meant for production use.
