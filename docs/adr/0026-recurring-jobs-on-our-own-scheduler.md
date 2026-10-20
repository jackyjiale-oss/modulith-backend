# 0026. Recurring jobs run on our own scheduler, not Hangfire

- Status: Accepted
- Date: 2026-10-20
- Deciders: Lee Jia Le

## Context
Plan 5 needs work that runs on a fixed interval on one instance at a time: the cleanup of what the earlier plans left to grow (processed outbox and inbox rows, idempotency keys, verification codes, refresh tokens, sessions, MFA challenges, trusted devices, hub tickets, notifications). Blueprint review 2.9 accepted Hangfire for background jobs.

The work in this plan is only interval-driven deletes. Nothing needs fire-and-forget or delayed jobs: reliable asynchronous work already has two engines, the outbox (domain events, with retries and a lease) and the notification delivery worker.

## Options considered
1. **Hangfire.** Mature, with a dashboard and many job kinds. But the core is LGPL-3.0, which is not in `build/licenses/allowed-licenses.json`, so it needs an ADR and an allow-list change; it owns a `hangfire` schema and its own migrations outside the module rule; its dashboard authorises through its own filter and cannot use the host's JWT (review 2.9); it depends on Newtonsoft.Json; and its fire-and-forget and delayed jobs have no consumer here.
2. **A hosted service per job** (each cleanup a `BackgroundService` with a `PeriodicTimer`). No new concept, but no lease: with several instances every instance runs every job at once, and nothing records when a job last ran or whether it failed. Tests would have to wait for a timer.
3. **An external scheduler** (a cron job, a Kubernetes `CronJob`, a cloud timer calling an endpoint). It moves the template's behaviour out of the template, and a developer without the platform gets no cleanup.
4. **Our own scheduler** in `Infrastructure.Common/Jobs`: `IRecurringJob`, a runner and a hosted service, with a SQL lease so a job runs on one instance at a time.

## Decision
We chose option 4 (decision D2 of the Plan 5 design).

- A job implements `IRecurringJob` (`Name`, `DefaultInterval`, `RunAsync`) and is registered by the module or building block that owns its data with `AddRecurringJob<TJob>()`, resolved in a scope of its own for every run.
- `RecurringJobBackgroundService` ticks every `Jobs:TickInterval` and calls `RecurringJobRunner.RunDueAsync`. The runner claims one due job at a time from `platform.JobStates` with the statement shape the outbox uses (`UPDATE ... OUTPUT` over an `UPDLOCK, READPAST` CTE that sets `LockedUntil`), runs it under a token cancelled when the lease ends, and writes the outcome and `NextRunAt = finish + interval` with an update guarded by the lease it claimed. A missed interval runs once; there is no catch-up burst. A job that throws is recorded `Failed` and retried at its next interval, and never blocks another job.
- The scheduler is switched off with `Jobs:Enabled = false` (tests, a future Worker split), and each job with `Jobs:{Name}:Enabled` and `Interval`.
- Each module owns the cleanup of its own tables (the module rule); the building blocks own the jobs for what they own (the outbox and inbox of each context, `platform.IdempotencyKeys`). Their shape, `BatchedCleanupJob`, is in [ADR 0028](0028-retention-policy-and-the-inbox-window.md).
- Hangfire stays a post-1.0 engine behind the same interface: switching is one new engine class and a licence ADR, and no cleanup job changes because each only implements `IRecurringJob`.

## Consequences
- Positive: no new package, licence or schema owner; the lease pattern is the one the outbox and the delivery worker already use and test; tests run one job by name through `RunDueAsync` with a fake clock; the state of every job is a row anyone can read.
- **This deviates from the accepted review (2.9).** The review took Hangfire for a wider need than the one that exists.
- **No dashboard and no manual triggering.** Job state is `platform.JobStates` and the `platform.jobs.*` metrics; an operator runs a job early by setting its `NextRunAt` in the database.
- **Intervals, not cron.** A job runs `Interval` after the end of its last run; there is no "at 02:00". A schedule that needs a wall-clock time is a reason to revisit this decision.
- **Time-bound by the lease.** A run is cancelled when `Jobs:LeaseDuration` (15 minutes) ends; a job that cannot finish in that time must work in bounded steps, as the batched cleanups do.
- **One job per claim**, the most overdue first, so a long job delays the others on that instance for the length of its run; another instance may take them meanwhile.
- Follow-up actions: a Hangfire engine behind `IRecurringJob`, with its licence ADR, if fire-and-forget or delayed jobs ever have a consumer.
