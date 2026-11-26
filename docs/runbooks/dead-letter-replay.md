# Runbook: replay abandoned outbox messages (dead letters)

Each module that raises events keeps an outbox in its own schema (`OutboxMessages`, [ADR 0007](../adr/0007-per-module-outbox.md)). The dispatcher retries a failed message and, after `Outbox:MaxAttempts` failed attempts (5 by default), gives up: the message stays with `ProcessedAt` null, its `AttemptCount` at the limit and its last `Error`. Nothing deletes an abandoned message and nothing retries it again. This page is the reviewed procedure to put it back in the queue once the cause is fixed. The module that owns the outbox is the only schema you touch; the examples use Auth's, `auth`.

## When

The gauge `platform.outbox.abandoned` (meter `TemplateName.Platform`, tag `context` is the module's name without `DbContext`, for example `Auth`) is above zero. Take the maximum across instances, never the sum ([`../building-blocks/infrastructure-common.md`](../building-blocks/infrastructure-common.md#cleanup-jobs)). The cleanup job `{Context}OutboxCleanup` sets it once a day, so the gauge can lag the table by up to a day: the `SELECT` below is the truth.

Do not replay before the cause is fixed: a replayed message that fails again is abandoned again after another `Outbox:MaxAttempts` attempts and wastes the time. A message whose secret cannot be decrypted (a lost key ring, [`restore-backup.md`](restore-backup.md)) fails the same way every time; do not replay it.

## Who

A database operator who may update the owning module's tables, with a second person reviewing the list of ids before step 2 (the statement changes production rows). It is operator SQL against the **owning module's own schema**, the one sanctioned exception to the rule that no one touches another module's tables; it is never run from another module's code, and never by the application.

## Steps

1. **Read the abandoned messages** and their errors. Set `@MaxAttempts` to your `Outbox:MaxAttempts` (5 unless you changed it):

   ```sql
   DECLARE @MaxAttempts int = 5;

   SELECT Type, COUNT(*) AS Messages, MIN(OccurredAt) AS Oldest, MAX(OccurredAt) AS Newest
   FROM auth.OutboxMessages
   WHERE ProcessedAt IS NULL AND AttemptCount >= @MaxAttempts
   GROUP BY Type;

   SELECT Id, Type, OccurredAt, AttemptCount, Error
   FROM auth.OutboxMessages
   WHERE ProcessedAt IS NULL AND AttemptCount >= @MaxAttempts
   ORDER BY OccurredAt;
   ```

   `Error` is the last exception of the last attempt (up to 2000 characters); the application log has the handler name and the attempt (`Outbox handler {HandlerName} failed on message {OutboxMessageId}`). Copy the errors into the ticket: step 2 clears them. **Fix the cause first** (a handler bug, a consumer that was down, a missing setting), deploy the fix, and choose the ids that the fix covers.
2. **Reset the chosen messages in one transaction**, with a row-count check against what you chose. Put the reviewed ids in the `IN` list and their number in `@Expected`:

   ```sql
   SET XACT_ABORT ON;
   DECLARE @MaxAttempts int = 5;
   DECLARE @Expected int = 3;

   BEGIN TRANSACTION;

   UPDATE auth.OutboxMessages
   SET AttemptCount = 0, NextAttemptAt = NULL, LockedUntil = NULL, Error = NULL
   WHERE Id IN ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000003')
     AND ProcessedAt IS NULL AND AttemptCount >= @MaxAttempts;

   IF @@ROWCOUNT <> @Expected
   BEGIN
       ROLLBACK TRANSACTION;
       THROW 50000, 'The number of rows reset differs from the ids chosen; nothing was changed.', 1;
   END;

   COMMIT TRANSACTION;
   ```

   The last two conditions make the statement refuse a message that was processed or is not abandoned in the meantime. The dispatcher claims a reset message at its next poll (`Outbox:PollingInterval`, 5 seconds by default) and runs every handler that has not succeeded yet.
3. **Watch it go.** Re-run the first `SELECT` of step 1: the reset messages leave the abandoned list and get a `ProcessedAt` once every handler has succeeded. The gauge falls after the next daily run of `{Context}OutboxCleanup`. A replayed message that fails again returns to abandoned after another `Outbox:MaxAttempts` attempts, with its new `Error`.

<!--#if (notifications)-->
A delivery that failed after its message left the outbox (an email or SMS that was dead-lettered by the sender) is not an outbox message: retry it with `POST /api/v1/admin/notifications/deliveries/{id}/retry` (permission `notifications.delivery.retry`; 202, 409 when it is not dead-lettered or its link has expired), not with this SQL.
<!--#endif-->

## Why replay is safe

The outbox delivers at least once, so every step of the chain is built to run twice:

- Inside the owning module, `OutboxMessageConsumers (OutboxMessageId, Name)` records each handler that succeeded, in the same save as its work. A replay skips those and runs only the handlers that failed. These rows are kept for an abandoned message (the cleanup deletes them only with a processed one).
- A handler that publishes an integration event to another module uses the outbox message id as the event id. Each consuming module records `InboxMessages (MessageId, Consumer)` in **its own schema**, in the same save as its rows, and skips an event it already recorded (ADR 0018). So a consumer that already succeeded is not run a second time.

One limit: an inbox row is deleted after `Retention:Inbox` (7 days by default) past its `ProcessedAt`. Replay within that window of the consumer's last success. Replaying a message older than that, one whose consumer had already done its work, can repeat the effect of that consumer: check the consumer's rows first.

## Verify

- The first `SELECT` of step 1 returns none of the replayed ids as abandoned, and `ProcessedAt` is set on them:

  ```sql
  SELECT Id, ProcessedAt, AttemptCount, Error FROM auth.OutboxMessages WHERE Id IN ('...');
  ```

- The effect happened once: the email, the notification or the record the event should have produced exists once.
- The gauge `platform.outbox.abandoned{context}` is back to the number you expect after the next daily run, and your alert is quiet.

## Roll back

Before the `COMMIT` of step 2, nothing is changed (the `ROLLBACK` in the script, or `ROLLBACK TRANSACTION` by hand). After it, a reset can be undone by abandoning the same ids again, as long as the dispatcher has not claimed them yet (it claims within seconds, so this is not a reliable undo):

```sql
DECLARE @MaxAttempts int = 5;
UPDATE auth.OutboxMessages SET AttemptCount = @MaxAttempts
WHERE Id IN ('...') AND ProcessedAt IS NULL;
```

What a handler did during a replay (a row written, a message queued) is not undone by the outbox; undo it as the consumer's own page describes.
