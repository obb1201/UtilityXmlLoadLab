# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Working rules

- Write in clear, simple English, in code comments, docs and replies.
- If a requirement is unclear, ask the user key questions before proceeding.
- Record changes in the change log in the `docs/` folder (`docs/CHANGELOG.md`). Add an entry whenever you make a notable change.

## Main goal: learning agentic engineering

The main goal of this project is for the user to understand **agentic engineering**: how to get reliable work from AI tools. The XML load experiment is the realistic workload to practise on. There are four topics:

1. **Agentic harness:** the agent loop and its tools, permissions, hooks, subagents, plan mode, worktrees and workflows.
2. **Context engineering:** what the agent sees and when, such as CLAUDE.md, the spec, docs, memory, compaction and subagent isolation.
3. **Skills and rules:** reusable instructions such as skills, slash commands, the team coding standard and the rules in this file.
4. **Agentic validation:** the work, check, fix loop, and evals that prove an agent's output is right.

When you work here:

- When it helps, say in one or two lines which topic a step belongs to, and why you chose that approach.
- **Close the loop.** After a change, run the matching check (build, tests, `deploy-db`, benchmark fingerprint, completeness report). Report the result as it is, including failures.
- When a task is a good chance to practise one of the topics, suggest it briefly. Do not build harness pieces unless the user asks.
- Put harness pieces in this repo, under `.claude/` (`settings.json`, `skills/`, `agents/`), so they can be read and changed like code.
- The practice list in [docs/AGENTIC-ENGINEERING.md](docs/AGENTIC-ENGINEERING.md) records ideas. Mark an item as done there when it is built.
- The data privacy rules below apply to harness pieces too: no real data in prompts, hooks, skills, evals or logs.

## Project status

The specification is [sap-xml-load-experiment-prompt.md](sap-xml-load-experiment-prompt.md). It is the source of truth for scope. The first working version is built and benchmarked: see [README.md](README.md), [docs/BENCHMARK-RESULTS.md](docs/BENCHMARK-RESULTS.md), and the design decisions in [docs/CHANGELOG.md](docs/CHANGELOG.md).

## Commands

```powershell
docker compose up -d                                              # SQL Server on localhost,14333 (password in .env)
dotnet build
dotnet test
dotnet run --project src/UtilityXmlLoadLab.Loader -- deploy-db    # runs sql/*.sql in order (idempotent)
dotnet run --project src/UtilityXmlLoadLab.Generator -- --count 10000 --reset
dotnet run --project src/UtilityXmlLoadLab.Loader -- load --approach A|B --dop 8
dotnet run --project src/UtilityXmlLoadLab.Loader -- check | approve --auto | promote --all | report
dotnet run --project src/UtilityXmlLoadLab.Benchmark -- --dops 1,4,8
dotnet run --project src/UtilityXmlLoadLab.Receiver               # CoreWCF on http://localhost:5080/InboxService.svc
```

The SQL password is in user secrets (`Sql:Password`, one shared UserSecretsId for all hosts) or in the environment variable `UXL_Sql__Password`.

## Things that are easy to break

- **The mapping lives in two places:** `StagingMap.cs` (approach B) and `sql/050_ApproachA_ShredBatch.sql` (approach A). Change both, then run the benchmark: its fingerprint comparison must report identical output.
- **Validation rules:** the field rules in `FieldValidator.cs` must match `dbo.fn_FieldProblem` (same accepted values, same error texts).
- **Parallel batches:** every per-batch statement must reach rows through a `BatchId` index (use index hints if needed), and it uses `MAXDOP 1`. Page locks are off on the shared tables. Breaking this causes deadlocks under parallel load.
- **SQL scripts:** after changing anything in `sql/`, run `deploy-db` again.

## What this is

A local experiment comparing two ways to do a **one-time load of ~1M SAP IS-U-style XML messages** into SQL Server:
- **A: T-SQL shredding** (`usp_ShredBatch @BatchId` using `xml.nodes()` + nested `CROSS APPLY`)
- **B: .NET parsing** (`XmlReader` streaming → `DataTable` → `SqlBulkCopy`, configurable parallelism)

The real goal is to prove the data arrived complete and correct before promotion. When trade-offs come up, favour **completeness/auditability first, then speed**.

## Architecture (data flow)

```
Generator ──► CoreWCF receiver ──► SapStaging.dbo.Inbox (raw XML, no parsing)
   └─(or direct insert for fast tests)──┘
Inbox ──► [A: usp_ShredBatch | B: .NET bulk loader] ──► staging tables + evidence tables
Evidence checks + referential checks ──► usp_ApproveBatch ──► usp_PromoteBatch (MERGE) ──► SapTarget
```

Key invariants that span components:
- **Both approaches write to the same staging and evidence tables.** The benchmark resets staging and runs A and B over the same Inbox data, so their outputs must be directly comparable.
- **Receiver does no parsing.** It inserts MessageId + raw XML + ReceivedAt and returns. A duplicate MessageId is skipped and still returns success (idempotent).
- **Staging has no FKs** (messages arrive in any order, and references may point to entities in later messages). Every staging row carries `MessageId` and `BatchId`. Keep indexes minimal.
- **SapTarget has full PKs/FKs**, so promotion must run in dependency order: Customer → Account → Premise → Installation → Meter → Contract → Agreement → MoveInOut → Payment.
- **Promotion only for Approved batches.** Auto-approve is allowed only when FailedCount = 0.
- Evidence: `LoadEvidence_Message` (per message, includes SHA2_256 hash of raw XML, expected vs staged row counts) and `LoadEvidence_Batch` (Staged/Approved/Promoted/Rejected). Referential findings (orphans, meters in more than one installation at once, overlapping contracts) must each be traceable to a MessageId.

## Constraints

- **Stack:** SQL Server 2022 Developer in Docker (`docker-compose.yml`), .NET 8+ LTS, **CoreWCF** (not classic WCF).
- **Layout:** one solution, a folder per project, layered as Core / DataAccess / Business / hosts (console or service).
- **C# style:** follow the team standard in the `csharp-code-reviewer` skill (it deliberately departs from .NET defaults: Allman braces, `b`-prefixed bools, `E`-prefixed enums, `ALL_CAPS` constants, avoid `var`, boundary-only exception handling).
- **SQL:** parameterized only. Scripts are **numbered and idempotent**.
- T-SQL shredding: no parent axis (`..`). Use `.value('(X/text())[1]', ...)`.
- Connection strings go in `appsettings.json` plus user secrets, never hard-coded.

## Data privacy (strict)

- Synthetic data only (e.g., Bogus). Never put real customer, account, or company data in code, scripts, tests, or logs.
- Use only generic, publicly documented IS-U concepts for names. Don't guess or reproduce any company-specific schema, codes, or rates.
- Any real XML samples supplied later must be anonymized, kept in a git-ignored folder, and never quoted in output.

## Deliverables expected by the spec

Working solution, `docker-compose.yml`, SQL scripts, and a README (setup, data generation, running each approach, approve/promote, reading evidence and benchmark output). Also run the 10k benchmark and report the results (elapsed time, msgs/sec, rows/sec, peak CPU if feasible, projected time for 1M).
