# 0004. SQL Server-ordered sequential GUIDs

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
The blueprint chose UUID v7 (`Guid.CreateVersion7()`) for primary keys, expecting time-ordered values to give sequential inserts into the clustered index. That holds on PostgreSQL, but not on SQL Server. UUID v7 puts the timestamp in the first bytes, while SQL Server compares `uniqueidentifier` values by bytes 10 to 15 first, most significant first. To SQL Server, v7 IDs look random, so inserts still split pages and fragment the clustered index (blueprint review, Section 2.4). v1 targets SQL Server only (ADR 0003).

## Options considered
1. `Guid.CreateVersion7()` — built into .NET and standards-based; but random-looking to SQL Server, so the clustered-index benefit does not apply.
2. `NEWSEQUENTIALID()` as a column default — sequential on the server; but the ID is unknown until the insert, so aggregates cannot raise events with their own ID, and it ties ID creation to the database.
3. `SequentialGuid.Create(DateTimeOffset)` in the SharedKernel — a Unix-millisecond timestamp in bytes 10 to 15 (SQL Server order) with random bytes elsewhere; the ID is created in application code, is sequential for SQL Server and is unit-testable.

## Decision
We chose option 3. `SharedKernel.SequentialGuid.Create(DateTimeOffset)` fills bytes 0 to 9 with cryptographically random data and writes the timestamp as a 48-bit big-endian integer into bytes 10 to 15. Time comes from the injected `TimeProvider`, never from the clock directly. The ordering is unit-tested with `System.Data.SqlTypes.SqlGuid`, which implements SQL Server's comparison rules.

## Consequences
- Positive: inserts into a GUID clustered index are append-mostly on SQL Server; IDs exist before the save, so aggregates can raise events that carry them; the ordering rule is covered by tests.
- Negative / trade-offs accepted: ordering is only at millisecond resolution (IDs created within the same millisecond sort randomly relative to each other); the IDs are not UUID v7, so other systems or a later PostgreSQL provider see no time ordering in the leading bytes; the timestamp is recoverable from the ID.
- Follow-up actions: revisit if PostgreSQL is added, where `Guid.CreateVersion7()` is the correct choice (see ADR 0003).
