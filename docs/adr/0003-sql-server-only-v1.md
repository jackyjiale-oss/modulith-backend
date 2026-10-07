# 0003. SQL Server only for v1

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
The blueprint offered a switch between SQL Server and PostgreSQL. EF Core migrations are provider-specific, so two providers mean two migration sets per module. Dapper read models use provider-specific SQL, and the blueprint's schema already relies on SQL Server-only types and features (`DATETIME2`, `ROWVERSION`, `ISJSON`). Supporting both would double the work for every module and every test run (blueprint review, D2).

## Options considered
1. SQL Server and PostgreSQL behind a provider switch — widest reach; but two migration sets per module, two SQL dialects for Dapper, and a doubled integration-test matrix.
2. SQL Server only for v1 — one migration set, one SQL dialect, one test container; excludes teams that need PostgreSQL until it is added.
3. PostgreSQL only — open source and cross-platform; but it does not match the SQL Server and IIS hosting this template targets first.

## Decision
We chose option 2. v1 targets SQL Server only. PostgreSQL becomes a post-v1 switch, decided separately.

## Consequences
- Positive: one migration set per module, one SQL dialect, a single Testcontainers image, and SQL Server types usable without abstraction.
- Negative / trade-offs accepted: teams that need PostgreSQL cannot use v1 as is.
- Follow-up actions: revisit when PostgreSQL is added; UUID v7 ordering and `datetime2(3)` choices are SQL Server-specific and must be re-evaluated then.
