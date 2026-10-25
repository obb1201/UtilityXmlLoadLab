# Benchmark results

- Date (UTC): 2026-10-25 09:13
- Server: SQL Server 16.0.4305.5 Developer Edition (64-bit), 16 logical CPUs, 6320 MB memory
- Client: 16 logical CPUs, .NET 10.0.13
- Data: 10,000 messages (9,700 passed, 300 failed incl. conflicting duplicates), 80,432 staged rows
- Each run starts from empty staging; one transaction per batch of 1,000 messages.

| Approach | DOP | Elapsed (s) | Msgs/s | Rows/s | Peak SQL CPU | Avg SQL CPU | Client CPU (s) | Failed batches | Projected 1M |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A: T-SQL shredding | 1 | 11.05 | 905 | 7,281 | 102% | 96% | 0.1 | 0 | 18 min 24 s |
| A: T-SQL shredding | 4 | 3.23 | 3,095 | 24,891 | 372% | 276% | 0.2 | 0 | 5 min 23 s |
| A: T-SQL shredding | 8 | 2.31 | 4,328 | 34,812 | 769% | 388% | 0.3 | 0 | 3 min 51 s |
| B: .NET XmlReader + SqlBulkCopy | 1 | 3.92 | 2,552 | 20,527 | 69% | 41% | 2.3 | 0 | 6 min 31 s |
| B: .NET XmlReader + SqlBulkCopy | 4 | 1.40 | 7,125 | 57,309 | 220% | 220% | 1.5 | 0 | 2 min 20 s |
| B: .NET XmlReader + SqlBulkCopy | 8 | 1.31 | 7,642 | 61,466 | 304% | 304% | 1.8 | 0 | 2 min 10 s |

CPU is in % of one core (the SQL container can go above 100%). Client CPU is the CPU time of the benchmark process (the parsing work of approach B).

## Output comparison

All runs wrote identical staging and evidence data (row counts and content checksums match for every table).

| Table | Rows | Content checksum |
|---|---:|---:|
| stg.MessageHeader | 9,700 | 178117208358 |
| stg.Customer | 1,308 | 14350391918 |
| stg.CustomerAddress | 1,686 | -39373970795 |
| stg.Account | 1,308 | 8752332929 |
| stg.Premise | 537 | -22416039903 |
| stg.Installation | 1,080 | 299330648711 |
| stg.Meter | 1,854 | 63300843493 |
| stg.MeterRegister | 1,761 | 529849755388 |
| stg.Contract | 4,222 | 1462947851971 |
| stg.Agreement | 3,695 | 91804803702 |
| stg.MoveInOut | 2,096 | 167531038403 |
| stg.Payment | 51,185 | 34107698187 |
| dbo.LoadEvidence_Message | 10,100 | -161804177484 |
| dbo.LoadEvidence_Batch | 10 | 606042896 |
