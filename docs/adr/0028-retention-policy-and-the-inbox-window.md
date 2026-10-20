# 0028. Retention policy, and an inbox window that outlasts redelivery

- Status: Accepted
- Date: 2026-10-20
- Deciders: Lee Jia Le

## Context
The earlier plans left tables that only grow: processed `OutboxMessages` and their `OutboxMessageConsumers`, `InboxMessages`, `platform.IdempotencyKeys`, and each module's expired codes, tokens, challenges, tickets and notifications. Plan 5 deletes them on a schedule (the scheduler is [ADR 0026](0026-recurring-jobs-on-our-own-scheduler.md)). Deleting is where mistakes cost data: a delete that is too eager removes evidence or breaks a guarantee, and one that is unbounded locks a table.

Two rows are not just clutter. An abandoned outbox message (it ran out of attempts) is a dead letter that an operator must see, not a processed row. And an inbox row is the proof that a consumer already handled an event: if it is deleted while the outbox can still redeliver that event, the consumer runs twice.

## Options considered
1. **Delete everything older than one global age.** Simple, but one number cannot fit an expired verification code (days) and a notification (months), and it would delete dead letters and shorten the inbox window.
2. **Keep rows until a human prunes them.** Nothing is lost, and nothing is safe: the tables grow without bound.
3. **A retention per kind of data, with batched deletes, the rules that protect dead letters and the inbox written down and validated.**

## Decision
We chose option 3.

- **Every delete is a loop of `DELETE TOP (@batch)`.** A run deletes at most `Jobs:MaxBatchesPerRun` batches (100) of `Retention:BatchSize` rows (1000) and stops early when a batch comes back short, so a run is bounded and a backlog drains over several runs. Each batch is its own statement and transaction.
- **A row goes only when strictly older than `now - retention`.** The clock is `TimeProvider`. Every predicate has a supporting index (a filtered one where the column is mostly null), and a test reads the execution plan of the largest.
- **A retention of `null` or `0` turns that job off.** A negative value fails validation on start.
- **The defaults** (the full table is in `docs/superpowers/specs/2026-10-19-platform-services-design.md`, section 3.2): outbox and inbox rows 7 days after `ProcessedAt`, daily; `IdempotencyKeys` past `ExpiresAt`, hourly; each module's own table by its own rule (verification codes and MFA challenges hourly, tokens, sessions, devices and notifications daily). Everything not marked hourly runs daily. Building blocks read `Retention:*`; modules read `{Module}:Retention:*`.
- **Dead letters stay.** The outbox job deletes only rows with a `ProcessedAt` before the cutoff. An abandoned message (`ProcessedAt IS NULL` and `AttemptCount >= MaxAttempts`) is never deleted by it; the job reports how many there are in the gauge `platform.outbox.abandoned{context}` on the `TemplateName.Platform` meter, after every run, even when the retention is off.
- **The consumers of a message go with it.** No foreign key joins `OutboxMessageConsumers` to `OutboxMessages`, so each batch deletes the consumers of the messages it deleted in the same transaction.
- **The inbox window must outlast redelivery.** The outbox retries a failing message for about an hour (5 s, 30 s, 2 min, 10 min, 1 h). `Retention:Inbox` is validated on start: a value above zero and below one day is an error. So a late retry can never meet a deleted inbox row and run its consumer twice. Zero or `null` switches the inbox cleanup off, which is safe.
- **Auth audit logs are not deleted by default** and refresh-token reuse detection is not weakened; those rules are set in the design (section 3.2) and arrive with the Auth cleanup jobs.

## Consequences
- Positive: tables stay bounded without a run ever holding a long lock; the dead letters an operator must act on survive and are counted; the one invariant between two cleanups (the inbox outlasts the outbox) is enforced where the value is read.
- **A processed outbox row is gone after a week.** What happened to a message older than that is no longer in the database; the audit trail is the Audit module's job (Plan 5b), not the outbox's.
- **Changing the outbox retry schedule changes the floor.** If a schedule is ever made longer than a day, the inbox minimum in `RetentionOptions` must grow with it.
- **A backlog drains slowly by design.** 100 batches of 1000 is 100000 rows per run per job; a larger backlog takes several daily runs unless `Jobs:{Name}:Interval` is shortened for a while.
- **The gauge shows the last run, not now.** It is set after each (daily) run and reported until the next one.
- Follow-up actions: the idempotency, Auth and Notifications cleanup jobs (Plan 5, Tasks 5 to 7); an archive target for audit logs (Plan 5b).
