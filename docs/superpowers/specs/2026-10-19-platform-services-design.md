# Plan 5: Platform services — design

- Status: Draft for review
- Date: 2026-10-19
- Roadmap row: 5 of `docs/blueprint-review.md` Section 6 (blueprint 8.1–8.4, 8.9, Phase 3, 11.7)
- Builds on: Plans 1–4, all merged (main `5a780e9`)

## 1. Intent

Write-back of what this plan is for. Correct anything that is wrong before the plan is written.

**Outcome.** A template user can run the API on more than one instance and leave it running for months. Today three things stop that: nothing deletes anything (outbox, inbox, idempotency keys, Auth and Notifications rows grow forever, and the docs promise "Plan 5" for each), every cache and rate-limit counter is per instance, and outbound HTTP has no timeout, retry or breaker policy.

**Success looks like.**

1. Every table the docs say "Plan 5 cleans" is cleaned on a schedule, by the module that owns it, in bounded batches, with retention configurable and tested against a real database.
2. With `Caching:Provider = redis`, the permission cache, the rate limiters and the SignalR push work across instances; with Redis down the API keeps serving (degraded, logged), and with the default `memory` provider nothing needs Redis.
3. Typed outbound HTTP calls get a named resilience pipeline; the breached-password check stays fail-open inside its 2 s budget.
4. Nothing here changes a public API route or a response shape.

**Said by you (the roadmap and docs):** HybridCache + Redis, background jobs with cleanup, HttpClient resilience, an audit trail through the outbox, and the retention and Redis items that Plans 2–4 deferred (Auth `MfaChallenges`, `TrustedDevices` and refresh-token chain; Notifications retention, SignalR backplane, per-instance hub ticket limit; outbox/inbox/idempotency purge; the Redis rate limiter).

**Assumed by me:** SQL Server stays the only database; the Worker project and the Dockerfile are Plan 6; no multi-tenancy, so cache keys carry no tenant; no endpoint needs output caching yet.

## 2. Decisions

| # | Decision | Why | Cost if you disagree |
|---|---|---|---|
| D1 | **Split row 5 into two plans.** Plan 5 = cache/Redis, jobs and retention, resilience. **Plan 5b = the Audit module** (own spec and plan). | The audit trail is a new module with its own schema, a cross-module event contract question (the interceptor must emit a change record that a module outside the changed module consumes, without every module's Contracts being referenced) and an overlap to settle with `auth.AuthAuditLogs`. It deserves its own design. The rest of row 5 is infrastructure with one shape. | Roadmap table gains a row "5b". If you want one plan, the Audit design is added as a part C. |
| D2 | **Own recurring-job scheduler, not Hangfire.** `IRecurringJob` plus a hosted `RecurringJobService` in `Infrastructure.Common`, with a SQL lease so a job runs on one instance at a time. Hangfire becomes a post-v1 engine behind the same interface. | The blueprint review accepted Hangfire (LGPL core) but the work in this plan is only fixed-interval deletes. Hangfire would need an ADR and an allow-list change (LGPL-3.0 is not in `build/licenses/allowed-licenses.json`), a `hangfire` schema it owns, a dashboard that cannot use JWT (review 2.9) and a Newtonsoft dependency. Its fire-and-forget and delayed jobs have no consumer: the outbox and the delivery worker already do reliable async work. The lease pattern is already used twice (outbox, deliveries). | This deviates from the accepted review. Switching later is one new engine class and a licence ADR; the cleanup jobs do not change because they only implement `IRecurringJob`. |
| D3 | **Each module owns its own cleanup jobs** (module rule: no access to another module's tables). Building blocks supply jobs for what they own: per-module outbox and inbox, and `platform.IdempotencyKeys`. | Keeps module boundaries and architecture tests intact. | none |
| D4 | **One optional project `TemplateName.Platform.Redis`**, referenced only by the host. It holds the Redis connection, the HybridCache L2, the Redis rate limiter and the SignalR backplane. | The simplification rule: a separate project only when excluding it removes a NuGet dependency. Removing it removes StackExchange.Redis and two Microsoft packages. | none |
| D5 | **The distributed rate limiter is our own fixed-window limiter on Redis** (one Lua `INCR` + `PEXPIRE` script), behind an `IFixedWindowLimiterFactory` that Web.Common defines with an in-memory default. Redis failure falls back to the in-memory limiter for that request. | The built-in limiter has no Redis backend (review 2.11); the only options are a third-party package, dividing limits per instance, or ~100 lines of our own. A community package adds a maintenance and licence risk for a feature this small. | If Redis is down every instance limits on its own, so the effective limit is `instances × limit` until Redis returns. |
| D6 | **Defaults keep Redis optional:** `Caching:Provider = memory`. Docker Compose adds a Valkey service (BSD-3, Redis-protocol compatible) that is off unless its profile is enabled. | Redis 7.4+ server licences are RSAL/SSPL/AGPL; Valkey keeps the template's "allowed licences" rule honest for what compose pulls. The client packages are MIT either way. | Swap the image line. |
| D7 | **Standard HTTP resilience from `Microsoft.Extensions.Http.Resilience`** (MIT), named pipelines `external-default` and `external-critical` plus a `hibp` pipeline sized for the 2 s budget. Pipelines for SMS and email providers are not created now (no consumer; the doc shows how to add one). | The blueprint names them, but a pipeline nobody uses is untested code. | none |
| D8 | **No jobs dashboard and no admin jobs API in v1.** Jobs are observable by metrics, logs and the `platform.JobStates` table. | Review 2.9 says the dashboard is off by default anyway; an admin API would need a Platform permission source for a feature with no user yet. | Add `GET /admin/platform/jobs` later; the table already holds what it returns. |

## 3. Architecture

### 3.1 Recurring jobs (`Infrastructure.Common/Jobs`)

- `IRecurringJob { string Name; TimeSpan DefaultInterval; Task<JobResult> RunAsync(CancellationToken) }`. `JobResult(int Processed)` is for the metric and the log line.
- `services.AddRecurringJob<TJob>()` (scoped per run). A module registers its jobs in its `Add{Module}Module`; `AddOutbox<TContext>()` registers the outbox and inbox jobs for that context.
- `RecurringJobService : BackgroundService` ticks every `Jobs:TickInterval` (default 30 s). On each tick it claims due jobs from `platform.JobStates` with the same lease statement shape as the outbox (`UPDATE … OUTPUT … WHERE NextRunAt <= @now AND (LockedUntil IS NULL OR LockedUntil < @now)`), runs each claimed job in its own scope, then writes `LastStartedAt`, `LastFinishedAt`, `LastStatus`, `LastError`, `LastProcessed` and the next `NextRunAt = finish + interval`. A missed interval runs once; there is no catch-up burst.
- A job that throws is logged, recorded as `Failed` and retried at the next interval; one failing job never blocks another. The lease length is `Jobs:LeaseDuration` (default 15 min) and a job is cancelled at lease end.
- Per-job config: `Jobs:{JobName}:Enabled` and `Interval`. `Jobs:Enabled = false` turns the service off (tests, a future Worker split).
- `platform.JobStates`: `Name` (PK, nvarchar 200), `NextRunAt`, `LockedUntil`, `LastStartedAt`, `LastFinishedAt`, `LastStatus`, `LastError` (2000), `LastProcessed`, `RowVersion`.
- Metrics (meter `TemplateName.Platform`): `platform.jobs.runs{job,outcome}`, `platform.jobs.duration{job}`, `platform.jobs.processed{job}`.

### 3.2 Cleanup, and what stays

All deletes are `DELETE TOP (@batch)` loops, at most `Jobs:MaxBatchesPerRun` batches (default 100) of `Retention:BatchSize` rows (default 1000), so a run is bounded and a backlog drains over several runs. Every predicate has a supporting index (the migrations in 3.5). A retention value of `null` or `0` disables that job.

| Owner | Data | Default retention | Interval |
|---|---|---|---|
| `Infrastructure.Common` (each outbox context) | Processed `OutboxMessages` and their `OutboxMessageConsumers` | 7 days after `ProcessedAt` | daily |
| `Infrastructure.Common` (each inbox context) | `InboxMessages` | 7 days after `ProcessedAt` (minimum 1 day) | daily |
| `Infrastructure.Common` | `platform.IdempotencyKeys` | rows past `ExpiresAt` | hourly |
| Auth | `VerificationCodes` | 7 days after expiry or consumption | hourly |
| Auth | `RefreshTokens` | 30 days after `ExpiresAt` (any session state) | daily |
| Auth | `UserSessions` (with their tokens by cascade) | 90 days after `RevokedAt` or `ExpiresAt` | daily |
| Auth | `MfaChallenges` | 1 day after expiry or consumption | hourly |
| Auth | `TrustedDevices` | 30 days after `ExpiresAt` or `RevokedAt` | daily |
| Notifications | `HubTickets` | 1 hour after expiry or consumption | hourly |
| Notifications | `Notifications` (their `Deliveries` go by cascade) | 180 days after `CreatedAt` | daily |
| Notifications | `InAppNotifications` (no foreign key to `Notifications`, so deleted by its own statement, read or unread) | 180 days after `CreatedAt` | daily |

Rules that matter:

- **Dead letters stay.** An abandoned outbox message (`ProcessedAt IS NULL`, `AttemptCount >= MaxAttempts`) and a dead-lettered delivery are not deleted by the 7-day job; the outbox job counts them in a gauge so they are visible.
- **The inbox window must outlast redelivery.** The outbox retries for about 1 hour at most (5 s, 30 s, 2 min, 10 min, 1 h). The inbox retention validator rejects a value below 1 day, so a late retry can never meet a deleted inbox row and run its consumer twice.
- **Refresh-token reuse detection is not weakened.** Tokens are deleted only after their own `ExpiresAt` plus the grace, when presenting them already fails.
- **`AuthAuditLogs` are not deleted by default.** The blueprint says "12 months hot, then archive" but names no archive. `Auth:Retention:AuditLogs` exists, is off by default, and when set deletes rows older than the value. An archive target is a decision for the Audit plan (5b).
- **`RecoveryCodes` and `PasswordHistory` are not time-based** (deleted with MFA disable, capped at five).
- Retention options live in each module's section (`Auth:Retention`, `Notifications:Retention`) and a building-block `Retention` section for the outbox, inbox and idempotency, each validated on start with ranges.

### 3.3 Redis (`TemplateName.Platform.Redis`)

- `Caching:Provider = memory | redis | none` (`none` disables L1 as well, for diagnosing). With `redis` the project registers one `IConnectionMultiplexer` (`abortConnect=false`) and `AddStackExchangeRedisCache` as the HybridCache L2.
- **Graceful degradation is ours to build, not assumed.** The L2 `IDistributedCache` is wrapped by a decorator that catches Redis exceptions, logs once per outage window, and short-circuits to a miss for `Caching:Redis:BreakDuration` (default 30 s) after `FailureThreshold` consecutive failures. A test with a failing fake proves a read, a write and a remove never throw.
- Key convention: `{InstanceName}{module}:{entity}:{id}` (no tenant). The existing `perm:{userId}` becomes `auth:perm:{userId}` at the module boundary; the 30 s lifetime stays.
- **Cross-instance invalidation is honest, not magic.** `RemoveAsync` clears Redis (L2) and the local L1. Another instance's L1 can serve a stale permission set for at most the 30 s L1 lifetime. This is the limit the review (2.7) and ADR 0016 already accept; the doc states it. Pub/sub L1 invalidation is a post-v1 item.
- Rate limiting: Web.Common defines `IFixedWindowLimiterFactory` (default in-memory, today's behaviour). The global, `auth-strict` and `notifications-hub-tickets` policies are rewritten to ask the factory for their limiter. Redis supplies a factory whose limiter runs the Lua script (`INCR`, `PEXPIRE` on first hit) and returns the same lease semantics and `Retry-After`.
- SignalR: `AddSignalR().AddStackExchangeRedis()` when the provider is `redis`. Hub tickets stay in SQL (single-use `UPDATE … OUTPUT`); only the per-user ticket counter moves to the shared limiter.
- Health: `/health/ready` gains a `redis` check (a `PING`) when the provider is `redis`, tagged `ready`. Redis down makes readiness **degraded**, not unhealthy, so a load balancer does not drain every instance for a cache outage.
- Docker Compose gains a `valkey` service behind a `redis` profile; `docker compose --profile redis up` starts it.

### 3.4 Resilience (`Infrastructure.Common/Http`)

- `AddExternalHttpClient(name, pipeline)` wraps `AddHttpClient` plus `AddResilienceHandler` with options bound from `Resilience:Pipelines:{name}` (total timeout, attempt timeout, retries, backoff, breaker thresholds), each validated.
- `external-default`: attempt 10 s, total 30 s, 3 retries, exponential backoff with jitter, breaker. `external-critical`: 5 retries. `hibp`: attempt 1 s, one retry, total 2 s, breaker; the checker's own 2 s linked token stays as the outer budget and its fail-open behaviour is unchanged.
- Polly's meter is added to the OpenTelemetry meters in `ObservabilityExtensions`.
- MailKit SMTP is not HTTP and keeps the delivery worker's own retry and dead-letter schedule.

### 3.5 Data and migrations

- `platform`: new `JobStates` table; index on `IdempotencyKeys(ExpiresAt)`.
- Every outbox context (`auth`, `sample`): index `OutboxMessages(ProcessedAt)` filtered `WHERE ProcessedAt IS NOT NULL`. Notifications inbox: index `InboxMessages(ProcessedAt)`.
- Auth: indexes on `VerificationCodes(ExpiresAt)`, `RefreshTokens(ExpiresAt)`, `UserSessions(ExpiresAt)` and `UserSessions(RevokedAt)` (filtered), `MfaChallenges(ExpiresAt)`, `TrustedDevices(ExpiresAt)`.
- Notifications: `HubTickets(ExpiresAt)`, `Notifications(CreatedAt)`; `InAppNotifications(CreatedAt)` if the existing (`UserId`, `CreatedAt`, `Id`) index cannot serve the predicate (the plan checks the execution plan).
- One new migration per context; none edits an applied migration.

## 4. Error handling and failure modes

| Failure | Behaviour |
|---|---|
| A cleanup job throws | Recorded `Failed`, logged, retried at the next interval; other jobs unaffected. |
| Two instances tick together | The lease lets one claim a job; the other skips it. A crashed instance's lease expires and the job is re-claimed. |
| Cleanup meets a long-held lock | Each batch is a short statement with the standard command timeout; a timeout fails that run, not the app. |
| Redis unreachable at start or later | `abortConnect=false`; cache reads miss to the database; limiters fall back to in-memory; readiness degraded; a warning is logged once per outage window. |
| Redis returns after an outage | The breaker half-opens after `BreakDuration` and traffic returns to Redis. |
| An outbound call keeps failing | The breaker opens and calls fail fast; the HIBP checker returns "not breached" as today. |

## 5. Testing

Written first, against a real SQL Server (and Valkey via Testcontainers) as in earlier plans.

- **Scheduler:** a job runs once with two `RecurringJobService` instances on one database; a throwing job is recorded and does not stop a second job; a lease that expires is re-claimed; a disabled job never runs; time moves with `FakeTimeProvider`.
- **Each cleanup job:** seed rows either side of the cutoff and assert exactly which rows go and which stay (including dead letters, abandoned outbox rows, unexpired tokens of live sessions, a used refresh token inside its lifetime); a backlog larger than one batch drains across runs; a retention of 0 disables the job; the migration's index is the one the predicate uses (checked from the execution plan for the largest tables).
- **Inbox invariant:** the retention validator rejects less than 1 day.
- **Redis:** the L2 decorator never throws when Redis fails and recovers after the break; permission invalidation on instance A is seen by instance B within the L1 lifetime (two hosts, one Valkey); the limiter shares a count across two hosts, returns `Retry-After`, and falls back in memory when Redis is stopped; the SignalR push reaches a client connected to the other instance.
- **Resilience:** a fake handler proves the retry count, the attempt timeout, the breaker opening and the HIBP total budget; options validators reject bad values.
- **Architecture and docs:** the new project is registered in `Assemblies.cs`, has its building-block document and passes layering tests; every error code and config key is documented; every new ADR is linked in `docs/README.md` and `docs/architecture/overview.md`.
- **Template smoke:** a generated project builds and its unit and architecture tests pass with the new project included.

## 6. Documentation, ADRs, packages

- ADRs: **0026** recurring jobs on our own scheduler instead of Hangfire (D2); **0027** optional Redis through one project, own rate limiter, Valkey in compose (D4–D6); **0028** retention policy and the inbox-window rule (3.2); **0029** standard HTTP resilience pipelines (D7).
- Docs updated in the same changes: `docs/building-blocks/infrastructure-common.md` (outbox cleanup, jobs, resilience, replace every "Plan 5" promise), a new `docs/building-blocks/platform-redis.md` (the test that lists building-block documents is edited with it), `web-common.md` (limiter factory), `docs/modules/auth.md` and `notifications.md` (retention, backplane, per-instance notes now true or removed), `docs/services/api.md` (`Caching`, `Jobs`, `Retention`, `Resilience` sections), `docs/architecture/overview.md`, `docs/README.md`, `README.md` and the template README.
- Packages (all need Central Package Management and an allowed licence): `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, `Microsoft.Extensions.Http.Resilience` (brings Polly), test-only `Testcontainers.Redis`. Each is MIT or Apache-2.0 and none charges usage fees; the plan's first task verifies this with the repo's licence command and records anything unexpected before use.
- `ALLOWED_SCOPES` in `build/scripts/check-commit-msg.sh` already has `infrastructure`, `web`, `auth`, `notifications`; the plan adds `platform` if the new project's commits need it.

## 7. Out of scope

The Audit module (Plan 5b); Hangfire and a dashboard; `IBackgroundJobScheduler.Enqueue/Schedule`; a Worker project; distributed locks (`IDistributedLock` has no caller); output caching; tag invalidation; pub/sub L1 invalidation; the notification digest job; archiving (as opposed to deleting) audit logs and deliveries; SMS and email provider pipelines; the Dockerfile, `migrate` mode and encrypted key ring (Plan 6).

## 8. Questions for you

1. **D1:** is splitting the Audit module into Plan 5b right, or should it stay in this plan?
2. **D2:** do you accept our own scheduler in place of Hangfire? This is the one change to an accepted review decision.
3. Are the default retention periods in 3.2 right? They follow blueprint 11.7, and add the MFA tables and the Notifications rows the blueprint does not list.
