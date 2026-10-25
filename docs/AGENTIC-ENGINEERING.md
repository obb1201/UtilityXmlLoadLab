# Learning agentic engineering

The main goal of this project is to learn **agentic engineering**: getting reliable work from AI
tools such as Claude Code. The XML load experiment is the workload. It is a good playground
because it has:
- a database and several apps;
- benchmarks with measurable results;
- strict rules (privacy, coding standard, lock rules);
- two pieces of code that must stay in sync.

The learning is split into four topics. Harness pieces live in this repo under `.claude/`, so they
can be reviewed like any other code.

## 1. Agentic harness

The harness is the agent loop around the model: tools, permissions, hooks, subagents, plan mode,
worktrees and workflows.

| # | What to try in this project | Status |
|---|---|---|
| 1.1 | **Permissions** (`.claude/settings.json`): allow `dotnet build`, `dotnet test` and `docker compose` without prompts. Deny reading `.env` and `samples-real/`. | Idea |
| 1.2 | **Hooks:** after an edit in `sql/`, run `deploy-db`. After a `.cs` edit, run `dotnet build`. Block any write to `samples-real/`. | Idea |
| 1.3 | **Plan mode:** plan a new message type (for example a meter reading) before any code is written, then compare the plan with the result. | Idea |
| 1.4 | **Worktrees:** try two tuning ideas for approach A side by side, then benchmark both. | Idea |
| 1.5 | **Multi-agent workflows:** fan out a review of A and B to several agents. This uses many tokens, so start with a small scope. | Idea |

## 2. Context engineering

Context engineering is about choosing what the agent sees, and when.

| # | What to try in this project | Status |
|---|---|---|
| 2.1 | **CLAUDE.md:** change or add a rule, then see how Claude's behaviour changes. | In use |
| 2.2 | **Spec as source of truth:** compare how well Claude works with and without [the spec](../sap-xml-load-experiment-prompt.md) in context. | In use |
| 2.3 | **Compaction:** after a long session is compacted, check what was kept and what was lost. Decide what must live in files instead. | Idea |
| 2.4 | **Memory:** see what Claude remembers between sessions, and what belongs in CLAUDE.md instead. | Idea |
| 2.5 | **Subagent isolation:** give a subagent only `sql/` and the lock rules, and compare its review with one done in the main session. | Idea |

## 3. Skills and rules

Skills and rules are reusable instructions the agent follows.

| # | What to try in this project | Status |
|---|---|---|
| 3.1 | **Team coding standard:** the `csharp-code-reviewer` skill sets the C# style. Check that new code follows it. | In use |
| 3.2 | **`/bench` skill:** generate 10k messages, run the benchmark and summarize the result. | Idea |
| 3.3 | **`/check-mapping` skill:** compare `StagingMap.cs` with `050_ApproachA_ShredBatch.sql` and list the differences. | Idea |
| 3.4 | **`sql-lock-reviewer` subagent:** check new SQL against the parallel-batch rules (BatchId index, `MAXDOP 1`, no page locks). | Idea |

## 4. Agentic validation (work, check, fix, and evals)

An agent's output is only useful if something checks it. Good checks are automatic, fast, and
independent of the agent that did the work.

These checks already exist in this project:
- **Benchmark fingerprint:** A and B must write identical data, checked on every benchmark run.
- **Completeness report and test manifest:** every injected fault must be caught. This works like an
  eval with known answers.
- **Unit tests:** 28 tests (`dotnet test`).
- **The deadlock fix:** a work, check, fix loop. The benchmark showed failed batches; after each fix
  the parallel run was repeated, until 20 runs had 0 failures
  ([details](DEADLOCK-ANALYSIS.md)).
- **Validate the validator:** the fingerprint once reported false differences, because its query had
  no `ORDER BY`. A check can be wrong too.

| # | What to try in this project | Status |
|---|---|---|
| 4.1 | **Stop hook:** Claude may not finish a task until `dotnet build` and `dotnet test` pass. | Idea |
| 4.2 | **Eval for a reviewer:** write SQL with known lock problems, and measure how many the `sql-lock-reviewer` finds. | Idea |
| 4.3 | **Mapping eval:** break the mapping on purpose in A or B, and check that the fingerprint catches it. | Idea |
| 4.4 | **Code review loop:** run `/code-review`, fix the findings, then review again. Compare with your own review. | Idea |

## Tips

- Start with 1.1 and 1.2. They are small, and you see the effect right away.
- After building something, ask Claude to explain how it works and when it runs.
- Record each new piece in [CHANGELOG.md](CHANGELOG.md), like any other change.
