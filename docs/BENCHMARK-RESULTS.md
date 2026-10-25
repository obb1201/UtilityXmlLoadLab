# Benchmark results: T-SQL shredding vs .NET parsing

Measured on 2026-10-25 on one Windows 11 machine. SQL Server 2022 Developer (16.0.4305.5) ran in Docker
with 16 logical CPUs and 6.3 GB memory. The .NET 10 client ran on the same machine, so both share the
same 16 CPUs. Raw reports: [benchmarks/benchmark-10k.md](benchmarks/benchmark-10k.md) and
[benchmarks/benchmark-100k.md](benchmarks/benchmark-100k.md).

## Data

- **Messages:** synthetic, from `generator --count N` with seed 42 and default faults. Mixed message
  types; on average 1,935 characters per message (largest 3,655).
- **10k run:** 10,000 messages, plus 100 duplicate arrivals. 300 messages fail on purpose
  (malformed, missing required element, bad value). 80,432 rows staged.
- **100k run:** 100,000 messages, plus 1,000 duplicate arrivals. 3,000 fail on purpose.
  801,381 rows staged.
- **Batches:** 1,000 messages per batch, one transaction per batch.
- **Same starting point:** every run started from empty staging over the same Inbox data.
- **What is timed:** the whole staging step, which includes the evidence:
  - parse,
  - validate,
  - write staging,
  - write message evidence,
  - finalize (recount staged rows, duplicates, completeness check, batch evidence).

## 10,000 messages (required run)

| Approach | DOP | Elapsed (s) | Msgs/s | Rows/s | Peak SQL CPU | Avg SQL CPU | Client CPU (s) | Failed batches | Projected 1M |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A: T-SQL shredding | 1 | 11.05 | 905 | 7,281 | 102% | 96% | 0.1 | 0 | 18 min 24 s |
| A: T-SQL shredding | 4 | 3.23 | 3,095 | 24,891 | 372% | 276% | 0.2 | 0 | 5 min 23 s |
| A: T-SQL shredding | 8 | 2.31 | 4,328 | 34,812 | 769% | 388% | 0.3 | 0 | 3 min 51 s |
| B: .NET XmlReader + SqlBulkCopy | 1 | 3.92 | 2,552 | 20,527 | 69% | 41% | 2.3 | 0 | 6 min 31 s |
| B: .NET XmlReader + SqlBulkCopy | 4 | 1.40 | 7,125 | 57,309 | 220% | 220% | 1.5 | 0 | 2 min 20 s |
| B: .NET XmlReader + SqlBulkCopy | 8 | 1.31 | 7,642 | 61,466 | 304% | 304% | 1.8 | 0 | 2 min 10 s |

All six runs wrote identical staging and evidence data: the row counts and content checksums
match for every table.

## 100,000 messages (more reliable projection)

The 10k runs are only 10 batches and 1 to 11 seconds long, so at DOP 8 most workers sit idle, and
start-up time counts for a lot. The 100k runs (100 batches) give a better base for the 1M projection.

| Approach | DOP | Elapsed (s) | Msgs/s | Rows/s | Peak SQL CPU | Avg SQL CPU | Client CPU (s) | Failed batches | Projected 1M |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A: T-SQL shredding | 1 | 104.08 | 961 | 7,700 | 143% | 112% | 0.6 | 0 | 17 min 20 s |
| A: T-SQL shredding | 4 | 35.01 | 2,856 | 22,888 | 426% | 375% | 1.0 | 0 | 5 min 50 s |
| A: T-SQL shredding | 8 | 24.54 | 4,076 | 32,662 | 775% | 653% | 0.2 | 0 | 4 min 05 s |
| B: .NET XmlReader + SqlBulkCopy | 1 | 35.75 | 2,797 | 22,418 | 145% | 87% | 12.8 | 0 | 5 min 57 s |
| B: .NET XmlReader + SqlBulkCopy | 4 | 14.63 | 6,835 | 54,772 | 330% | 252% | 15.8 | 0 | 2 min 26 s |
| B: .NET XmlReader + SqlBulkCopy | 8 | 11.98 | 8,351 | 66,919 | 448% | 332% | 19.0 | 0 | 1 min 59 s |

All six runs wrote identical output here as well.

- **CPU columns:** CPU is in % of one core. 800% means 8 cores busy. SQL CPU is sampled with
  `docker stats` about once per second, so short runs have few samples.
- **Client CPU:** the CPU time of the benchmark process, which is where approach B does its parsing.
- **Variation:** repeated runs varied by about ±20% (for example, A at DOP 8 took 18 s to 26 s at
  100k).

## The rest of the cutover

These steps are the same for both approaches (100k messages, single run, wall clock):

| Step | 100k | Linear projection for 1M |
|---|---:|---:|
| Direct bulk insert into the Inbox (generator) | 8.3 s | ~1.5 min |
| `usp_CheckReferences` (all batches at once) | 9.0 s | ~1.5 to 3 min (it joins across all batches, so it may grow faster than linear) |
| `usp_PromoteBatches` (all 100 batches, one transaction) | 13.0 s | ~2 to 4 min |
| Delivery through CoreWCF, 8 parallel calls (2,000-message sample) | 781 msgs/s | ~21 min; in real life the sender's rate decides this |

## What the numbers say

1. **Both approaches fit a cutover window easily.** At DOP 8, staging 1M messages is projected at
   about 2 minutes (B) or 4 minutes (A). Even fully serial, it is 6 minutes (B) or 17 minutes (A).
   With the checks and promotion added, the whole run after receipt is under 15 minutes for either
   approach at DOP 8.
2. **B is about 2 to 3 times faster at every DOP**, and it puts less load on SQL Server:
   - B at DOP 8 used about 3.3 cores of SQL Server CPU on average; A at DOP 8 used about 6.5.
   - The parsing work moves to the client, about 19 s of client CPU per 100k messages.
   - On a shared production server, this difference matters more than elapsed time.
3. **A needed tuning before it was competitive.** The first working version took 88 s for 10k
   messages:
   - Parallel plans for `xml.nodes()` over a 1,000-message batch were about 15 times slower than
     serial plans. With `OPTION (MAXDOP 1)` it took 12 s.
   - Even then, A scales mainly by running batches side by side, and its SQL CPU grows with every
     added batch.
4. **Completeness and auditability are equal.** Both approaches:
   - give the same result for every message,
   - write the same staged rows and evidence (checked by fingerprint on every run),
   - catch 100% of the injected technical faults (completeness report, manifest checks).

   This is by design: the evidence is built by shared procedures, and B applies exactly the same
   field rules as A.
5. **Robustness under parallel load.** Running batches in parallel first caused deadlocks in the
   shared finalize step: page locks, and access through the MessageId keys. The batch rolled back,
   was logged, and was flagged by the completeness report, so nothing was lost silently. The fix
   was to reach rows only through BatchId indexes, turn page locks off, and use MAXDOP 1 per batch.
   After the fix there were 0 failed batches in 20 parallel runs at 10k and 100k. Details:
   [DEADLOCK-ANALYSIS.md](DEADLOCK-ANALYSIS.md).

## Recommendation

**Use approach B (.NET XmlReader + SqlBulkCopy) at DOP 4 to 8 for the real load.** It is faster and
lighter on SQL Server, and it gives the same evidence. Its mapping is a single declarative table
(`StagingMap.cs`) that is easy to change when the real XML turns out messier than the spec.

Keep approach A as an independent cross-check. Running it over the same Inbox and comparing
fingerprints is a cheap second opinion on the mapping before the real cutover.

## Caveats

- **Shared machine:** the client and SQL Server share one machine and 16 CPUs. On separate servers,
  B usually gains more, because its parsing CPU no longer competes with SQL Server.
- **Synthetic data:** messages are about 2 KB. Real IDocs can be larger and deeper, which costs
  `xml.nodes()` (A) more than streaming (B). Re-run the benchmark on anonymized real samples before
  deciding.
- **Linear projections:** the 1M figures assume linear scaling from 100k. The reference check and
  promotion work across all batches and should be measured at 1M before the cutover is planned:
  `generator --count 1000000` is supported.
