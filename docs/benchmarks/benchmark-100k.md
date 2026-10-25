# Benchmark results

- Date (UTC): 2026-10-25 09:23
- Server: SQL Server 16.0.4305.5 Developer Edition (64-bit), 16 logical CPUs, 6320 MB memory
- Client: 16 logical CPUs, .NET 10.0.13
- Data: 100,000 messages (97,000 passed, 3,000 failed incl. conflicting duplicates), 801,381 staged rows
- Each run starts from empty staging; one transaction per batch of 1,000 messages.

| Approach | DOP | Elapsed (s) | Msgs/s | Rows/s | Peak SQL CPU | Avg SQL CPU | Client CPU (s) | Failed batches | Projected 1M |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A: T-SQL shredding | 1 | 104.08 | 961 | 7,700 | 143% | 112% | 0.6 | 0 | 17 min 20 s |
| A: T-SQL shredding | 4 | 35.01 | 2,856 | 22,888 | 426% | 375% | 1.0 | 0 | 5 min 50 s |
| A: T-SQL shredding | 8 | 24.54 | 4,076 | 32,662 | 775% | 653% | 0.2 | 0 | 4 min 05 s |
| B: .NET XmlReader + SqlBulkCopy | 1 | 35.75 | 2,797 | 22,418 | 145% | 87% | 12.8 | 0 | 5 min 57 s |
| B: .NET XmlReader + SqlBulkCopy | 4 | 14.63 | 6,835 | 54,772 | 330% | 252% | 15.8 | 0 | 2 min 26 s |
| B: .NET XmlReader + SqlBulkCopy | 8 | 11.98 | 8,351 | 66,919 | 448% | 332% | 19.0 | 0 | 1 min 59 s |

CPU is in % of one core (the SQL container can go above 100%). Client CPU is the CPU time of the benchmark process (the parsing work of approach B).

## Output comparison

All runs wrote identical staging and evidence data (row counts and content checksums match for every table).

| Table | Rows | Content checksum |
|---|---:|---:|
| stg.MessageHeader | 97,000 | -268213923332 |
| stg.Customer | 13,089 | -102229614780 |
| stg.CustomerAddress | 17,030 | 174871660326 |
| stg.Account | 13,089 | 70164858796 |
| stg.Premise | 5,470 | 154526521153 |
| stg.Installation | 10,890 | 2205689594677 |
| stg.Meter | 18,546 | 489509017595 |
| stg.MeterRegister | 17,742 | 4212391109233 |
| stg.Contract | 41,547 | 10199105576102 |
| stg.Agreement | 36,578 | 308608118789 |
| stg.MoveInOut | 21,056 | 1057004299460 |
| stg.Payment | 509,344 | 815227370122 |
| dbo.LoadEvidence_Message | 101,000 | -57282859675 |
| dbo.LoadEvidence_Batch | 100 | 6307332421 |
