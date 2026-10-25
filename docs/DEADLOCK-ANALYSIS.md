# Deadlock analysis: parallel batch loading

When batches were loaded in parallel (DOP 4 to 16), some failed with SQL Server error 1205
("deadlock victim"). This document explains why, how it was found, and the rules that prevent it.

## Summary

Batches that ran at the same time ended up touching each other's rows, even though each batch only
needs its own 1,000 messages. When two transactions each hold a lock the other one wants, SQL Server
breaks the tie by killing one of them.

There were three separate causes, found one after another by reading the deadlock graphs from SQL
Server's built-in `system_health` session.

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 1 | No speed-up from parallelism | Key-range lock in `usp_BeginBatch` | Per-batch application lock |
| 2 | Deadlocks in `usp_FinalizeBatch` | Page locks and parallel plans | Row locks only, `MAXDOP 1` |
| 3 | Deadlocks at 100k, DOP 8 (5 of 100 batches) | Optimizer used the MessageId index | Index hints to the BatchId index |

## Background: why parallel batches can collide

Each batch runs as one transaction: delete old rows, insert staging and evidence rows, then finalize.
At DOP 4 or 8, several batches do this at the same moment on the **same shared tables**: `Inbox`,
`LoadEvidence_Message`, and the staging tables. A transaction keeps its locks until it commits.

## Cause 1: blocking from a key-range lock

- **What happened:** `usp_BeginBatch` checked the batch status using `WITH (UPDLOCK, HOLDLOCK)`. For a
  batch with no evidence row yet, `HOLDLOCK` locks a **key range** in `LoadEvidence_Batch`, not just
  one row.
- **The effect:** that range covered the place where other batches would later insert their evidence
  row. Approach A holds its transaction open for the whole shredding, so every other batch waited.
  The waits showed in `sys.dm_exec_requests` as `LCK_M_RS_U`.
- **The result:** A at DOP 4 was no faster than at DOP 1, and SQL CPU stayed at about one core. This
  was blocking, not yet deadlocks.
- **Fix:** a per-batch application lock (`sp_getapplock`). It only stops two sessions from loading
  the *same* batch, and it never blocks other batches.

## Cause 2: page locks and parallel plans

Once batches really ran in parallel, real deadlocks appeared in `usp_FinalizeBatch` on
`LoadEvidence_Message`.

- **Page locks:** rows of different batches sit next to each other, often on the same 8 KB data
  page. SQL Server sometimes locked a whole **page** instead of single rows, so one batch could block
  another.
- **Parallel plans:** the deadlock graph also showed an `exchangeEvent`. A statement ran with several
  threads, and those threads waited on other sessions in a way SQL Server could not resolve.
- **Fix:** page locks are turned off on the shared tables (`ALLOW_PAGE_LOCKS = OFF`, in
  `sql/010_Staging_Tables.sql`), so only row locks are used. The small per-batch statements in
  `usp_BeginBatch` and `usp_FinalizeBatch` use `OPTION (MAXDOP 1)`.

## Cause 3: the optimizer used the wrong index (the main cause)

This one remained after the first fixes. The deadlock graphs pointed at **`PK_LoadEvidence_Message`**
and later at **`PK_Inbox`**, both keyed on `MessageId`.

**What the statements looked like.** They were written "per batch", for example:

```sql
UPDATE i SET Status = e.ValidationStatus
FROM dbo.Inbox i
JOIN dbo.LoadEvidence_Message e ON e.MessageId = i.MessageId
WHERE i.BatchId = @BatchId;
```

**What SQL Server chose.** Because the join is on `MessageId`, the optimizer often decided to walk the
**MessageId index**. MessageIds are random GUIDs, so that index mixes all batches together.

**What went wrong.** While scanning, the statement takes an update lock on each row it reads,
including rows that belong to **other batches**. Those rows had just been inserted by other,
uncommitted transactions, so they were locked:

```
Batch 12: holds its new rows  ──waits for──►  a row of batch 13
Batch 13: holds its new rows  ──waits for──►  a row of batch 12
                   → deadlock, SQL Server kills one batch
```

**Why tests did not catch it at first.** It only appears with real concurrency and enough data. It
was rare at 10k messages, but at 100k with DOP 8 it hit 5 of 100 batches.

**Fix.** Index hints make evidence and Inbox rows always reachable only through their **BatchId
index**: `WITH (INDEX(CIX_LoadEvidence_Message_BatchId))` and `WITH (INDEX(IX_Inbox_BatchId))` in
`sql/040_Staging_Batch_Lifecycle.sql`. A batch can then only ever see its own rows, so there is
nothing to deadlock on.

## Why no data was lost

The audit design worked as intended:

- every deadlocked batch was rolled back completely,
- it was logged in `dbo.LoadRunBatch` with error 1205,
- the completeness report (`loader report completeness`) showed `CHECK` for the missing evidence.

After all three fixes, 20 parallel runs at 10k and 100k messages had zero failed batches.

## Rules for the real load

1. **Reach rows through the BatchId index.** Every per-batch statement must do this. Writing
   `WHERE BatchId = @BatchId` is not enough, because the optimizer may still choose another path. Use
   an index hint when the statement joins on another key.
2. **Keep page locks off** on tables that parallel batches write to.
3. **Use `MAXDOP 1` for per-batch statements.** Parallelism comes from running batches side by side,
   not from parallel plans inside one batch.
4. **Never use `HOLDLOCK` / serializable checks** on shared tables inside a long batch transaction.
   Use an application lock for "one session per batch".
5. **Test at scale.** After any change to the batch procedures, run the benchmark at 100k with DOP 8
   or more and confirm 0 failed batches.

## How to investigate a new deadlock

1. Find the failed batches and their errors:
   ```sql
   SELECT RunId, BatchId, ErrorMessage
   FROM SapStaging.dbo.LoadRunBatch
   WHERE IsSucceeded = 0;
   ```
2. Read the newest deadlock graph from the `system_health` session. The ring buffer is more current
   than the file target:
   ```sql
   DECLARE @rb xml =
   (
       SELECT CAST(t.target_data AS xml)
       FROM sys.dm_xe_session_targets t
       JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
       WHERE s.name = 'system_health' AND t.target_name = 'ring_buffer'
   );

   SELECT TOP (5) e.n.value('@timestamp', 'datetime2') AS DeadlockAt, e.n.query('.') AS DeadlockGraph
   FROM @rb.nodes('//event[@name="xml_deadlock_report"]') e(n)
   ORDER BY DeadlockAt DESC;
   ```
3. **Read the graph.** In `resource-list`, check the object and index. In `process-list`, find the
   statements involved. An index whose key is not BatchId (for example a primary key on MessageId)
   means a statement is touching other batches' rows. Apply rule 1.
