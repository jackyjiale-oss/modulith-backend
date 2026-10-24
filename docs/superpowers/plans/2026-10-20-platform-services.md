# Modulith Backend: Action Plan (Plan 5: Platform Services)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A template user can run the API on several instances and leave it running for months: every table the docs say "Plan 5 cleans" is cleaned on a schedule, the permission cache, rate limiters and SignalR push work across instances when Redis is configured (and the API keeps serving when Redis is down), and typed outbound HTTP calls get a named resilience pipeline.

**Architecture:** An own recurring-job scheduler in `Infrastructure.Common` (one hosted service, a `platform.JobStates` lease table, `IRecurringJob`); each owner registers its cleanup jobs (building blocks for outbox, inbox and idempotency, Auth and Notifications for their own tables). One optional project, `TemplateName.Platform.Redis`, referenced only by the host, supplies the HybridCache L2, a Lua fixed-window limiter behind a Web.Common factory interface, the SignalR backplane and a degraded-not-unhealthy readiness check. Resilience is `Microsoft.Extensions.Http.Resilience` pipelines bound from configuration.

**Tech Stack:** Plan 4 stack plus `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, `Microsoft.Extensions.Http.Resilience` (Polly) and, tests only, `Testcontainers.Redis` (runs Valkey).

**Spec:** `docs/superpowers/specs/2026-10-19-platform-services-design.md` (approved 2026-10-20). Read it first; section numbers below refer to it.

## Global Constraints

Everything in Plans 1-4's Global Constraints applies: Central Package Management, lock files and NuGet Audit; allowed licences `MIT`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause` and no usage fees; `TimeProvider` only; no abbreviations; `Async` suffix and `CancellationToken` last; no type ending in `Manager`, `Helper`, `Util`, `Utility`, `Impl`, `Dto`, `Settings` or `Info`; no identifier containing `TemplateName_`; Conventional Commits (header at most 72 characters, scopes from `build/scripts/check-commit-msg.sh`); `dotnet format --verify-no-changes` before every commit; warnings are errors in Release; every new error code in `en`, `ms` and `zh-Hans`; the matching `docs/` page updated in the same change.

- **Workflow:** one branch `feature/platform-services` from an up-to-date `main`, **one commit per task**, one PR at the end. Never push to `main`. Commits end with `Co-Authored-By: <the model that wrote it> <noreply@anthropic.com>`.
- **Running tests:** `dotnet build -c Release`, then `dotnet test tests/{Project} --no-build -c Release --filter "FullyQualifiedName~{Name}"`; the full suite is `dotnet test --no-build -c Release` (never `-m:1`: it runs zero tests). Integration tests need Docker. Never run `wsl --terminate` or restart Docker from a task.
- **Jobs (3.1):** `Jobs:Enabled` true, `Jobs:TickInterval` 30 s, `Jobs:LeaseDuration` 15 min, `Jobs:MaxBatchesPerRun` 100; per job `Jobs:{JobName}:Enabled` and `Interval`. Job names are PascalCase without dots (`AuthRefreshTokensCleanup`) so they work as environment-variable keys. A missed interval runs once; `NextRunAt = finish + interval`.
- **Retention (3.2):** `Retention:BatchSize` 1000; a retention of `null` or `0` disables its job; deletes are `DELETE TOP (@batch)` loops; a row goes only when **strictly older** than `now - retention`. Defaults: outbox and inbox 7 days after `ProcessedAt` (inbox minimum 1 day), idempotency rows past `ExpiresAt` (hourly), Auth `VerificationCodes` 7 days and `MfaChallenges` 1 day (hourly), Auth `RefreshTokens` 30 days after `ExpiresAt`, `UserSessions` 90 days after `RevokedAt` or `ExpiresAt`, `TrustedDevices` 30 days after `ExpiresAt` or `RevokedAt`, Notifications `HubTickets` 1 hour (hourly), `Notifications` and `InAppNotifications` 180 days after `CreatedAt`; everything not marked hourly runs daily. `Auth:Retention:AuditLogs` is off by default. Dead letters (abandoned outbox rows, dead-lettered deliveries) are not touched by the 7-day jobs; the outbox job reports abandoned rows in a gauge.
- **Redis (3.3):** `Caching:Provider = memory | redis | none`, default `memory`; Redis down means cache reads miss to the database, limiters fall back to memory, readiness is **degraded** (not unhealthy), one warning per outage window. L1 lifetime of a permission set stays 30 s; key `{InstanceName}{module}:{entity}:{id}`, so `perm:{userId}` becomes `auth:perm:{userId}`.
- **Resilience (3.4):** pipelines `external-default` (attempt 10 s, total 30 s, 3 retries, exponential backoff with jitter, breaker), `external-critical` (5 retries), `hibp` (attempt 1 s, one retry, total 2 s, breaker); the HIBP checker keeps its own 2 s linked token and fails open.
- **No public route or response shape changes.** The OpenAPI snapshot must not change.

## Decisions this plan takes

The spec fixes the design; these fill gaps it leaves. Each costs one edit if you disagree.

| # | Decision | Why |
|---|---|---|
| P1 | **All schema changes land in Task 2**, one migration per context (platform, auth, sample, notifications), so no later task edits a model. | The spec asks for one migration per context; every cleanup predicate then already has its index. |
| P2 | `AddOutbox<TContext>()` registers the outbox job, **`AddInbox<TContext>()` the inbox job**, `AddInfrastructureCommon` the idempotency job. | Only Notifications has an inbox; the spec's "AddOutbox registers both" cannot work. (Spec 3.1 is corrected.) |
| P3 | Where the spec says "after expiry or consumption" the predicate is `ExpiresAt` alone for rows that live minutes to days (verification codes, MFA challenges, hub tickets). | One index serves it; a consumed row stays at most one lifetime longer than the wording. Trusted devices and sessions need `ExpiresAt OR RevokedAt`, so those get both indexes. |
| P4 | Added indexes beyond spec 3.5: filtered `TrustedDevices(RevokedAt)`, `AuthAuditLogs(OccurredAt)` (unless an index already leads with it), and `InAppNotifications(CreatedAt)` unconditionally. | Every predicate needs a supporting index; the existing `(UserId, CreatedAt, Id)` cannot serve a `CreatedAt`-only predicate. |
| P5 | The Notifications job never deletes a notification that still has a `Pending` delivery. | A pending delivery must not lose its parent; expired ones are not pending. |
| P6 | Platform.Redis options added: `Caching:Redis:FailureThreshold` (default 3) and `CommandTimeout` (default 500 ms, applied to connect, sync and async timeouts). Connection string `ConnectionStrings:Redis`. | The spec names the threshold without a value; without a short command timeout a dead Redis adds seconds to every request until the breaker opens. |
| P7 | Breaker defaults for every pipeline: failure ratio 0.5, minimum throughput 10, sampling 30 s, break 30 s. | The spec names "breaker" without numbers. |
| P8 | `RecurringJobRunner.RunDueAsync(Func<string, bool>? include, CancellationToken)` is the test seam: the hosted service calls it with `null`; tests pass a job-name filter so they run one job. | Tests must run exactly one job without turning the others off. |
| P9 | Test hosts set `Jobs:Enabled = false`; tests run jobs through the runner or the job itself. | Same as `Outbox:Enabled = false`. |

## Review Focus

Inputs the spec implies and a person will meet, most likely first. Each is pinned by a named test in the owning task.

1. **Redis down at start and mid-run:** reads, writes and removes never throw, readiness degrades, limiters count in memory, a notification still delivers when the SignalR backplane is down (Tasks 9-11).
2. **A backlog larger than one batch and rows exactly on the cutoff:** drains over several runs; a row exactly at `now - retention` stays; a live session's unexpired tokens and a used token inside its lifetime stay (Tasks 4-7).
3. **Two instances ticking together, a job longer than its lease, a crashed instance:** one runs it, the lease is re-claimed after expiry, a job past the lease is cancelled and recorded `Failed` (Task 3).
4. **Hostile configuration:** retention `0` or `null` disables, a negative or sub-day inbox value is rejected on start, `Jobs:Enabled=false`, a `Jobs:{Unknown}` section is ignored, duplicate job names fail at registration (Tasks 3-5).
5. **A shared limiter key:** the same IPv6 or user key from two instances, window rollover, a key that lost its expiry, `Retry-After` an integer of at least 1, policies not sharing counters (Task 10).

---

### Task 1: Packages, licence check, commit scope, project skeleton

**Files:**
- Modify: `Directory.Packages.props`, `TemplateName.slnx`, `build/scripts/check-commit-msg.sh` (add `platform` to `ALLOWED_SCOPES`), `src/BuildingBlocks/TemplateName.Infrastructure.Common/TemplateName.Infrastructure.Common.csproj`, `tests/TemplateName.IntegrationTests/TemplateName.IntegrationTests.csproj`, `src/Host/TemplateName.Api/TemplateName.Api.csproj`
- Create: `src/BuildingBlocks/TemplateName.Platform.Redis/TemplateName.Platform.Redis.csproj` (library, no code yet; mirror `TemplateName.Web.Common.csproj`)

**Produces:** the five packages restored under Central Package Management; `Microsoft.Extensions.Caching.Hybrid` and `Microsoft.Extensions.Http.Resilience` referenced by Infrastructure.Common; the three Redis packages referenced by Platform.Redis; `Testcontainers.Redis` by the integration tests; the host referencing Platform.Redis.

- [ ] **Step 1:** Add the `PackageVersion` entries (latest stable at the time; Microsoft packages on the 10.0.x line the solution already uses) in a new `Label="Platform"` group, and the `PackageReference`s above.
- [ ] **Step 2:** Run `dotnet restore`, then the licence command from `build/licenses/README.md`. Expected: exit 0. If a package fails or declares no licence, read its terms and repository, record the evidence in `build/licenses/README.md`, and stop and ask before widening any list. Read each package's terms for usage fees; none may charge.
- [ ] **Step 3:** Run `dotnet list package --vulnerable --include-transitive`. Expected: no advisory. A patched transitive pin follows the existing "Security pins" pattern in `Directory.Packages.props`.
- [ ] **Step 4:** Run `dotnet build -c Release` and `dotnet test --no-build -c Release`. Expected: green, nothing changed in behaviour.
- [ ] **Step 5: Commit** `chore(deps): add the packages plan 5 needs`.

### Task 2: Schema, indexes and one migration per context

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Jobs/JobStateRecord.cs`; migrations `AddJobStatesAndExpiryIndex` (platform, `Idempotency/Migrations`), `AddCleanupIndexes` in Auth, Sample and Notifications (each module's `Infrastructure/Persistence/Migrations`)
- Modify: `Idempotency/PlatformDbContext.cs`, `Outbox/OutboxModelBuilderExtensions.cs`, `Inbox/InboxModelBuilderExtensions.cs`, the Auth and Notifications entity configurations
- Test: `tests/TemplateName.IntegrationTests/Persistence/CleanupSchemaTests.cs`

**Produces:** `internal sealed class JobStateRecord { string Name; DateTime NextRunAt; DateTime? LockedUntil; DateTime? LastStartedAt; DateTime? LastFinishedAt; string? LastStatus; string? LastError; int? LastProcessed; byte[] RowVersion }` in `platform.JobStates` (`Name` PK nvarchar 200, `LastError` 2000, `datetime2(3)` UTC); every index in spec 3.5 plus P4.

- [ ] **Step 1: Write the failing test** `CleanupSchemaTests.Every_cleanup_predicate_has_its_index` as a theory over `(schema.table, leadingColumn, filtered)`: `platform.IdempotencyKeys/ExpiresAt`; `auth.OutboxMessages/ProcessedAt (filtered)` and `sample.OutboxMessages/ProcessedAt (filtered)`; `notify.InboxMessages/ProcessedAt`; `auth.VerificationCodes/ExpiresAt`; `auth.RefreshTokens/ExpiresAt`; `auth.UserSessions/ExpiresAt` and `auth.UserSessions/RevokedAt (filtered)`; `auth.MfaChallenges/ExpiresAt`; `auth.TrustedDevices/ExpiresAt` and `.../RevokedAt (filtered)`; `auth.AuthAuditLogs/OccurredAt`; `notify.HubTickets/ExpiresAt`; `notify.Notifications/CreatedAt`; `notify.InAppNotifications/CreatedAt`. It queries `sys.indexes` and `sys.index_columns` for an index whose first key column is the named one (and `has_filter = 1` where marked). Add `JobStates_has_the_columns_of_the_spec` (column names, `Name` primary key, `LastError` length 2000, `RowVersion` is `rowversion`).
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.IntegrationTests --no-build -c Release --filter "FullyQualifiedName~CleanupSchemaTests"`. Expected: FAIL (missing indexes and table).
- [ ] **Step 3: Implement** the entity, the mappings (`ApplyOutbox` and `ApplyInbox` gain the index so every outbox or inbox context gets it) and the indexes, then generate the four migrations with the command in `CLAUDE.md` (the platform one with `--project src/BuildingBlocks/TemplateName.Infrastructure.Common --context PlatformDbContext --output-dir Idempotency/Migrations`). Check `AddRoleAndAuditLogListIndexes` before adding the `AuthAuditLogs(OccurredAt)` index. Never edit an existing migration.
- [ ] **Step 4: Run** the filter, then `dotnet ef migrations has-pending-model-changes` for each of the four contexts. Expected: PASS and "No changes".
- [ ] **Step 5: Commit** `feat(persistence): add the schema the cleanup jobs need`.

### Task 3: The recurring-job scheduler

**Files:**
- Create: `Infrastructure.Common/Jobs/{IRecurringJob,JobResult,JobsOptions,RecurringJobRunner,RecurringJobBackgroundService,RecurringJobMetrics,JobServiceCollectionExtensions}.cs`
- Modify: `InfrastructureServiceCollectionExtensions.cs` (options, metrics, runner, hosted service), `src/BuildingBlocks/TemplateName.Web.Common/Observability/ObservabilityExtensions.cs` (add the `TemplateName.Platform` meter and `Polly`), the integration factory (`Jobs:Enabled = false`)
- Test: `tests/TemplateName.UnitTests/Infrastructure/Jobs/JobsOptionsTests.cs`, `tests/TemplateName.IntegrationTests/Jobs/RecurringJobRunnerTests.cs`, test jobs `CountingJob` and `ThrowingJob` under `tests/TemplateName.IntegrationTests/Jobs/`

**Interfaces - Produces:**
- `public interface IRecurringJob { string Name { get; } TimeSpan DefaultInterval { get; } Task<JobResult> RunAsync(CancellationToken cancellationToken); }`; `public readonly record struct JobResult(int Processed);`
- `services.AddRecurringJob<TJob>() where TJob : class, IRecurringJob` (scoped per run; a duplicate `Name` throws `InvalidOperationException` when the runner first builds its registry).
- `public sealed class RecurringJobRunner { Task<int> RunDueAsync(Func<string, bool>? include, CancellationToken cancellationToken); }` (returns the number of jobs run).
- `JobsOptions` (`Jobs`): `Enabled`, `TickInterval` [5 s, 1 h], `LeaseDuration` [1 min, 1 day], `MaxBatchesPerRun` [1, 10000]; per-job `Enabled` and `Interval` read from `Jobs:{Name}`.
- Meter `TemplateName.Platform`: `platform.jobs.runs{job,outcome}`, `platform.jobs.duration{job}`, `platform.jobs.processed{job}`.

- [ ] **Step 1: Write the failing tests.** Unit: `Defaults_match_the_spec`; `Out_of_range_values_are_rejected` (TickInterval 1 s, LeaseDuration 10 s, MaxBatchesPerRun 0); `Job_interval_and_enabled_come_from_its_section_else_the_default`; `Unknown_job_sections_are_ignored`. Integration (`RecurringJobRunnerTests`, a second runner instance over the same database for the two-instance cases, `Factory.Time` for time): `A_due_job_runs_once_and_its_state_row_is_written` (`LastStatus = Succeeded`, `LastProcessed`, `NextRunAt = finish + interval`); `Two_runners_ticking_together_run_a_job_once`; `A_throwing_job_is_recorded_Failed_and_does_not_stop_another_job`; `A_job_that_outlives_its_lease_is_cancelled_and_recorded_Failed`; `A_crashed_runners_lease_is_reclaimed_after_it_expires` (set `LockedUntil` in the future: skipped; past it: run); `A_missed_interval_runs_once_not_in_a_burst` (move the clock 10 intervals: one run); `A_disabled_job_never_runs`; `Duplicate_job_names_fail_at_first_use`; `Metrics_record_one_run_per_outcome`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Jobs"` in both projects. Expected: FAIL.
- [ ] **Step 3: Implement.** The claim is one `UPDATE ... OUTPUT inserted.Name` of due, unlocked-or-expired rows shaped like `OutboxDispatcher`'s `ClaimSqlTemplate`, after inserting a row (`NextRunAt = now`) for each registered job that has none; each claimed job runs in its own scope under a token cancelled at lease end; the finish `UPDATE` is guarded by the claimed `LockedUntil`. `RecurringJobBackgroundService` is a `BackgroundService` over `PeriodicTimer(TickInterval, timeProvider)` that returns at once when `Jobs:Enabled` is false and logs, never throws, on a database outage (as `OutboxBackgroundService` does).
- [ ] **Step 4: Run** the filter, then `dotnet test --no-build -c Release`. Expected: green.
- [ ] **Step 5: Commit** `feat(infrastructure): add the recurring job scheduler`.

### Task 4: Retention foundation and outbox cleanup

**Files:**
- Create: `Infrastructure.Common/Jobs/{BatchedCleanupJob,BatchedDelete}.cs`, `Infrastructure.Common/Persistence/RetentionOptions.cs`, `Infrastructure.Common/Outbox/{OutboxCleanupJob,OutboxAbandonedGauge}.cs`
- Modify: `OutboxServiceCollectionExtensions.cs`, `InfrastructureServiceCollectionExtensions.cs` (`Retention` options)
- Test: `tests/TemplateName.UnitTests/Infrastructure/Jobs/RetentionOptionsTests.cs`, `tests/TemplateName.IntegrationTests/Outbox/OutboxCleanupJobTests.cs`

**Interfaces - Produces:**
- `public sealed class RetentionOptions` (`Retention`): `BatchSize` [1, 10000] = 1000; `Outbox` and `Inbox` as `TimeSpan?` = 7 days (zero or null disables; `Inbox` below 1 day is a validation error on start).
- `public abstract class BatchedCleanupJob(DbContext context, TimeProvider timeProvider, IOptions<JobsOptions> jobs, IOptions<RetentionOptions> retention) : IRecurringJob`, abstract `Name`, `DefaultInterval`, `TimeSpan? RetentionPeriod`, `string DeleteSql` (a `DELETE TOP (@Batch)` statement using `@Cutoff`); `RunAsync` returns `JobResult(0)` when `RetentionPeriod` is null or zero, else loops through `BatchedDelete`. Modules derive from it in Tasks 5-7.
- `static Task<int> BatchedDelete.RunAsync(DbContext context, string sql, DateTime cutoff, int batchSize, int maxBatches, CancellationToken cancellationToken)` (stops when a batch deletes fewer than `batchSize` rows or after `maxBatches`).
- `OutboxCleanupJob<TContext>`: name `{Auth|Sample}OutboxCleanup` (context name without `DbContext`), daily, deletes `OutboxMessages` with `ProcessedAt < cutoff` and their `OutboxMessageConsumers` (check the foreign key; if it does not cascade, delete the consumers of the same batch in the same transaction). After a run it sets the abandoned gauge `platform.outbox.abandoned{context}` to the count of `ProcessedAt IS NULL AND AttemptCount >= MaxAttempts`.

- [ ] **Step 1: Write the failing tests.** Unit: `Retention_defaults_match_the_spec`; `Inbox_retention_below_one_day_is_rejected` (23 h) and `Inbox_retention_of_zero_or_null_is_allowed_meaning_off`; `Negative_retention_is_rejected`; `BatchSize_range_is_enforced`. Integration (`OutboxCleanupJobTests`, `BatchSize` and `MaxBatchesPerRun` set small): `Processed_messages_older_than_seven_days_go_with_their_consumers`; `A_message_processed_exactly_seven_days_ago_stays` and one second earlier goes; `Pending_and_abandoned_messages_stay_and_the_gauge_counts_the_abandoned`; `A_backlog_of_five_with_a_batch_of_two_drains_over_three_runs`; `Retention_zero_or_null_deletes_nothing`; `Cleanup_predicate_uses_the_filtered_index` (run the SELECT form under `SET SHOWPLAN_XML ON`; the plan names the index and has no scan of the clustered index).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Retention|FullyQualifiedName~OutboxCleanup"`. Expected: FAIL.
- [ ] **Step 3: Implement.** `AddOutbox<TContext>()` also calls `AddRecurringJob<OutboxCleanupJob<TContext>>()`.
- [ ] **Step 4: Run** the filter; then `dotnet test --no-build -c Release`. Expected: green.
- [ ] **Step 5: Docs:** `docs/building-blocks/infrastructure-common.md` (jobs, retention, outbox cleanup; replace the outbox "Plan 5" promise), `docs/adr/0026-recurring-jobs-on-our-own-scheduler.md`, `docs/adr/0028-retention-policy-and-the-inbox-window.md`; link both in `docs/README.md` and `docs/architecture/overview.md` (a test checks this).
- [ ] **Step 6: Commit** `feat(outbox): clean up processed outbox rows on a schedule`.

### Task 5: Inbox and idempotency cleanup

**Files:**
- Create: `Infrastructure.Common/Inbox/InboxCleanupJob.cs`, `Infrastructure.Common/Idempotency/IdempotencyCleanupJob.cs`
- Modify: `InboxServiceCollectionExtensions.cs`, `InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/TemplateName.IntegrationTests/Inbox/InboxCleanupJobTests.cs`, `tests/TemplateName.IntegrationTests/Idempotency/IdempotencyCleanupJobTests.cs`

**Produces:** `InboxCleanupJob<TContext>` (name `NotificationsInboxCleanup`, daily, `Retention:Inbox`) registered by `AddInbox<TContext>()`; `IdempotencyCleanupJob` (name `IdempotencyKeysCleanup`, hourly, deletes `ExpiresAt < now`, switched off only by `Jobs:IdempotencyKeysCleanup:Enabled`) registered by `AddInfrastructureCommon`.

- [ ] **Step 1: Write the failing tests.** Inbox: `Rows_processed_more_than_seven_days_ago_go_and_newer_stay`; `A_row_inside_the_outbox_retry_window_is_never_deleted_even_at_the_minimum` (retention 1 day, row 2 h old stays; this is the rule that stops a late retry from running a consumer twice); `Retention_off_deletes_nothing`; `Backlog_drains_across_runs`. Idempotency: `Expired_keys_go_unexpired_and_in_progress_keys_stay`; `A_key_exactly_at_ExpiresAt_stays` and one second later goes; `Request_with_a_deleted_key_runs_again` (existing semantic: expired keys are reclaimed); `Cleanup_predicate_uses_the_ExpiresAt_index`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~InboxCleanup|FullyQualifiedName~IdempotencyCleanup"`. Expected: FAIL.
- [ ] **Step 3: Implement** both jobs on `BatchedCleanupJob`; `AddInbox<TContext>()` and `AddInfrastructureCommon` register them.
- [ ] **Step 4: Run** the filter and the Notifications inbox tests. Expected: green.
- [ ] **Step 5: Docs:** `infrastructure-common.md` (inbox and idempotency cleanup, the inbox-window rule), `docs/modules/notifications.md` (inbox retention).
- [ ] **Step 6: Commit** `feat(infrastructure): clean up the inbox and idempotency keys`.

### Task 6: Auth cleanup jobs

**Files:**
- Create: `Infrastructure/Retention/{AuthRetentionOptions,VerificationCodesCleanupJob,RefreshTokensCleanupJob,UserSessionsCleanupJob,MfaChallengesCleanupJob,TrustedDevicesCleanupJob,AuditLogsCleanupJob}.cs` (under `src/Modules/Auth/TemplateName.Modules.Auth/`)
- Modify: `AuthModule.cs` (options validated on start, six `AddRecurringJob` calls)
- Test: `tests/TemplateName.IntegrationTests/Auth/AuthCleanupJobTests.cs`, `tests/TemplateName.UnitTests/Auth/AuthRetentionOptionsTests.cs`

**Produces:** `Auth:Retention` options (`VerificationCodes` 7 d, `RefreshTokens` 30 d, `Sessions` 90 d, `MfaChallenges` 1 d, `TrustedDevices` 30 d, `AuditLogs` null; each `TimeSpan?`, zero or null off). Jobs `AuthVerificationCodesCleanup` and `AuthMfaChallengesCleanup` hourly (predicate `ExpiresAt < @Cutoff`); `AuthRefreshTokensCleanup` daily (`ExpiresAt < @Cutoff`, any session state); `AuthSessionsCleanup` daily (`RevokedAt < @Cutoff OR ExpiresAt < @Cutoff`, tokens by cascade: verify the foreign key and delete them first in the same batch if it does not cascade); `AuthTrustedDevicesCleanup` daily (`ExpiresAt < @Cutoff OR RevokedAt < @Cutoff`); `AuthAuditLogsCleanup` daily (`OccurredAt < @Cutoff`, off by default). All derive from `BatchedCleanupJob` on `AuthDbContext`.

- [ ] **Step 1: Write the failing tests.** Unit: defaults match the spec; `AuditLogs_is_null_by_default`; negative values rejected. Integration, per job a boundary pair (cutoff exactly: stays, one second older: goes): `Verification_codes_...`, `Refresh_tokens_...`, `Sessions_...`, `Mfa_challenges_...`, `Trusted_devices_...`, `Audit_logs_...`; plus `A_used_refresh_token_inside_its_lifetime_stays`; `Unexpired_tokens_of_a_live_session_stay_and_expired_ones_of_the_same_session_go`; `A_revoked_session_goes_after_ninety_days_with_its_tokens`; `A_session_expired_long_ago_but_never_revoked_goes`; `A_revoked_trusted_device_with_a_far_expiry_goes_thirty_days_after_revocation`; `Audit_logs_are_kept_by_default`; `Refresh_token_reuse_detection_still_works_for_a_token_inside_its_lifetime` (present a used token twice: session revoked as before); `Retention_zero_disables_every_auth_job` (theory over the six names); `Cleanup_predicates_use_their_indexes` for `RefreshTokens` and `UserSessions`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AuthCleanup|FullyQualifiedName~AuthRetention"`. Expected: FAIL.
- [ ] **Step 3: Implement** the options and jobs; the SQL uses the table and column names of the entity configurations.
- [ ] **Step 4: Run** the filter, then `dotnet test --no-build -c Release`. Expected: green, including the existing Auth tests.
- [ ] **Step 5: Docs:** `docs/modules/auth.md` (retention table, config keys; replace every "Plan 5" promise and the "(later)" admin-reset line at `auth.md:697` if the text is stale).
- [ ] **Step 6: Commit** `feat(auth): clean up expired Auth rows on a schedule`.

### Task 7: Notifications cleanup jobs

**Files:**
- Create: `Infrastructure/Retention/{NotificationsRetentionOptions,HubTicketsCleanupJob,NotificationsCleanupJob,InAppNotificationsCleanupJob}.cs` (under `src/Modules/Notifications/TemplateName.Modules.Notifications/`)
- Modify: `NotificationsModule.cs`, `Endpoints/HubTicketRateLimit.cs` (drop the "until Plan 5's cleanup job" remark)
- Test: `tests/TemplateName.IntegrationTests/Notifications/NotificationsCleanupJobTests.cs`, `tests/TemplateName.UnitTests/Notifications/NotificationsRetentionOptionsTests.cs`

**Produces:** `Notifications:Retention` (`HubTickets` 1 h, `Notifications` 180 d, `InAppNotifications` 180 d). Jobs `NotificationsHubTicketsCleanup` hourly (`ExpiresAt < @Cutoff`); `NotificationsRowsCleanup` daily (`CreatedAt < @Cutoff` and no delivery `Pending`, P5; deliveries by cascade, verify the foreign key); `NotificationsInAppCleanup` daily (`CreatedAt < @Cutoff`, read or unread, no foreign key to `Notifications`).

- [ ] **Step 1: Write the failing tests:** boundary pairs for the three jobs; `Deliveries_go_with_their_notification`; `A_notification_with_a_pending_delivery_stays`; `Dead_lettered_deliveries_go_with_a_180_day_old_notification_not_before`; `In_app_rows_go_read_or_unread_without_their_notification`; `A_consumed_ticket_goes_after_it_expires_not_at_consumption`; `A_ticket_still_valid_stays`; `Retention_zero_disables_each_job`; `Cleanup_predicates_use_their_indexes` for `Notifications` and `InAppNotifications`; unit tests for defaults and validation.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~NotificationsCleanup|FullyQualifiedName~NotificationsRetention"`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Task 6.
- [ ] **Step 4: Run** the filter, then `dotnet test --no-build -c Release`. Expected: green.
- [ ] **Step 5: Docs:** `docs/modules/notifications.md` (retention table and keys; the "Plan 5" cleanup promises become facts).
- [ ] **Step 6: Commit** `feat(notifications): clean up old notifications and tickets`.

### Task 8: HTTP resilience pipelines

**Files:**
- Create: `Infrastructure.Common/Http/{ResilienceOptions,ResiliencePipelines,HttpClientBuilderExtensions}.cs`
- Modify: `AuthModule.cs` (the `hibp` client), `ObservabilityExtensions.cs` (meter `Polly`, if not added in Task 3)
- Test: `tests/TemplateName.UnitTests/Infrastructure/Http/ResiliencePipelineTests.cs`, `tests/TemplateName.UnitTests/Auth/HibpResilienceTests.cs`

**Produces:** `ResiliencePipelines.Default = "external-default"`, `.Critical = "external-critical"`, `.Hibp = "hibp"`; `public static IHttpClientBuilder AddExternalHttpClient(this IServiceCollection services, string name, string pipeline = ResiliencePipelines.Default)` binding `Resilience:Pipelines:{pipeline}` (`TotalTimeout`, `AttemptTimeout`, `Retries`, `BaseDelay`, breaker `FailureRatio`, `MinimumThroughput`, `SamplingDuration`, `BreakDuration`; defaults per Global Constraints and P7), each validated on start (attempt not above total, retries 0-10, positive timeouts, ratio in (0, 1]). Pipelines use the DI `TimeProvider`.

- [ ] **Step 1: Write the failing tests** with a fake `HttpMessageHandler` and `FakeTimeProvider`: `Default_pipeline_retries_three_times_on_503_then_returns_the_last_response` (4 handler calls); `Critical_pipeline_retries_five_times`; `A_4xx_is_not_retried`; `Attempt_timeout_cancels_a_hung_attempt_after_ten_seconds`; `Total_timeout_bounds_all_attempts_at_thirty_seconds`; `Breaker_opens_after_the_threshold_and_the_next_call_fails_fast_without_reaching_the_handler`; `Breaker_half_opens_after_BreakDuration`; `Options_validators_reject_attempt_above_total_zero_timeout_and_negative_retries`; `Config_overrides_a_pipeline_value`. Auth (`HibpResilienceTests`, real checker with the `hibp` pipeline): `A_hung_server_returns_not_breached_within_the_two_second_budget`; `A_503_returns_not_breached_after_one_retry`; `An_open_breaker_returns_not_breached_without_a_request`; `A_body_over_the_cap_is_still_refused` (existing behaviour kept).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Resilience"`. Expected: FAIL.
- [ ] **Step 3: Implement** `AddExternalHttpClient` (`AddHttpClient` plus `AddResilienceHandler`, exponential backoff with jitter) and switch the Auth `hibp` registration to it.
- [ ] **Step 4: Run** the filter and the existing `HibpBreachedPasswordCheckerTests`. Expected: green, unchanged.
- [ ] **Step 5: Docs:** `infrastructure-common.md` (resilience, how to add a pipeline for a future SMS or email provider), `docs/adr/0029-standard-http-resilience-pipelines.md`, `docs/modules/auth.md` (HIBP behaviour).
- [ ] **Step 6: Commit** `feat(infrastructure): add standard HTTP resilience pipelines`.

### Task 9: Platform.Redis core: cache, availability and health

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Platform.Redis/{RedisCachingOptions,RedisAvailability,ResilientDistributedCache,RedisHealthCheck,PlatformRedisServiceCollectionExtensions}.cs`, `Infrastructure.Common/Caching/{CachingProvider,CachingOptions}.cs`, `docs/building-blocks/platform-redis.md`, `docs/adr/0027-optional-redis-through-one-project.md`, `tests/TemplateName.IntegrationTests/Infrastructure/ValkeyFixture.cs`
- Modify: `Infrastructure.Common` registration (the `Caching` options and the `none` provider), `Auth/Infrastructure/Authorization/PermissionCache.cs` (key `auth:perm:{userId:D}`) and the comments that name `perm:`, `src/Host/TemplateName.Api/Program.cs` (`AddPlatformRedis`) and `appsettings.json` (`Caching`), `docker-compose.yml`, `tests/TemplateName.ArchitectureTests/{Assemblies.cs,DocumentationTests.cs}`, `LayeringTests.cs` if it must learn the new project
- Test: `tests/TemplateName.UnitTests/Platform/{ResilientDistributedCacheTests,RedisAvailabilityTests,CachingProviderTests}.cs`, `tests/TemplateName.IntegrationTests/Redis/{RedisCacheTests,RedisDegradedTests}.cs`

**Interfaces - Produces:**
- `public enum CachingProvider { Memory, Redis, None }`; `CachingOptions { CachingProvider Provider }` (`Caching`); with `None`, `HybridCacheOptions.DefaultEntryOptions.Flags` disables both layers.
- `RedisCachingOptions` (`Caching:Redis`): `InstanceName` ("TemplateName:"), `BreakDuration` 30 s, `FailureThreshold` 3, `CommandTimeout` 500 ms (P6); connection string `ConnectionStrings:Redis`, required and validated on start only when the provider is `Redis`.
- `public sealed class RedisAvailability(TimeProvider, IOptions<RedisCachingOptions>, ILogger<RedisAvailability>) { bool IsAvailable { get; } void RecordSuccess(); void RecordFailure(Exception exception); }` (opens after `FailureThreshold` consecutive failures for `BreakDuration`, logs one warning per outage window).
- `ResilientDistributedCache : IDistributedCache` decorating the Redis L2: never throws, answers a miss while the circuit is open.
- `public static IServiceCollection AddPlatformRedis(this IServiceCollection services, IConfiguration configuration)`: a no-op unless `Caching:Provider = redis`; else registers one `IConnectionMultiplexer` (`abortConnect=false`, timeouts from `CommandTimeout`), `AddStackExchangeRedisCache` wrapped by the decorator, and a `redis` health check (a `PING`, failure status `Degraded`, tag `ready`).
- `ValkeyFixture` (a `Testcontainers.Redis` container on a pinned `valkey/valkey` tag; compose uses the same tag and a test checks it, like `MailpitFixture.Image`); compose service `valkey` behind profile `redis`, published on `127.0.0.1:6379` only.
- `docs/building-blocks/platform-redis.md` exists and `Assemblies.BuildingBlocks` and the expected list in `Every_building_block_has_a_document` include `platform-redis` (the project lives at `src/BuildingBlocks/TemplateName.Platform.Redis`).

- [ ] **Step 1: Write the failing tests.** Unit: `Read_write_refresh_and_remove_never_throw_when_the_inner_cache_fails`; `After_three_consecutive_failures_the_inner_cache_is_not_called_for_thirty_seconds` and `is_called_again_after_the_break`; `A_success_resets_the_failure_count`; `One_warning_is_logged_per_outage_window`; `Provider_defaults_to_memory`; `Provider_none_makes_every_permission_read_hit_the_database` (use `RecordingDbConnectionFactory`); `Redis_provider_without_a_connection_string_fails_on_start`; `Permission_key_is_auth_perm_userid`. Integration (two hosts from `Factory.WithWebHostBuilder` sharing the SQL database, the clock and one Valkey, provider `redis`): `A_permission_set_cached_by_host_A_is_served_to_host_B_without_a_database_read`; `Invalidating_on_A_is_seen_by_B_after_the_thirty_second_L1_lifetime` (advance `Factory.Time` past 30 s); `Valkey_stopped_the_api_still_checks_permissions_from_the_database`; `Valkey_stopped_readiness_is_Degraded_not_Unhealthy` (HTTP 200, body `Degraded`); `Valkey_back_the_cache_recovers_after_the_break`; `Default_memory_provider_needs_no_Redis` (no `IConnectionMultiplexer` registered). Architecture: `Every_building_block_has_a_document` expects `platform-redis`; layering lets only the host reference the project.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Platform|FullyQualifiedName~Redis|FullyQualifiedName~CachingProvider"` plus the architecture tests. Expected: FAIL.
- [ ] **Step 3: Implement.** Decorate `IDistributedCache` with `services.Replace` and a factory (no new package). Write `platform-redis.md` (configuration, degradation, the honest 30 s cross-instance limit from spec 3.3, how to remove the project) and ADR 0027.
- [ ] **Step 4: Run** the filters, `docker compose --profile redis config`, then `dotnet test --no-build -c Release`. Expected: green.
- [ ] **Step 5: Commit** `feat(platform): add the optional Redis project with a resilient cache`.

### Task 10: The limiter factory and the Redis limiter

**Files:**
- Create: `Web.Common/Security/{IFixedWindowLimiterFactory,InMemoryFixedWindowLimiterFactory}.cs`, `Platform.Redis/{RedisFixedWindowLimiter,RedisFixedWindowLimiterFactory}.cs`
- Modify: `Web.Common/Security/HttpSecurityExtensions.cs`, `Notifications/Endpoints/HubTicketRateLimit.cs` and its caller in `NotificationsModule.AddHub`, `PlatformRedisServiceCollectionExtensions.cs`
- Test: `tests/TemplateName.UnitTests/Web/FixedWindowLimiterFactoryTests.cs`, `tests/TemplateName.IntegrationTests/Redis/RedisRateLimitTests.cs`

**Produces:** `public interface IFixedWindowLimiterFactory { RateLimiter Create(string policy, string partitionKey, int permitLimit, TimeSpan window); }`; the in-memory default (`TryAddSingleton`, behaviour identical to today's `FixedWindowRateLimiter`: `QueueLimit = 0`, auto-replenish); the `global`, `auth-strict` and `notifications-hub-tickets` policies build their partitions with `RateLimitPartition.Get(key, ...)` through the factory; the Redis factory replaces it when the provider is `redis`. The Redis limiter runs one Lua script: `INCR key`; `PEXPIRE key windowMs` when the count is 1 **or the key has no expiry**; returns the count and `PTTL`. Key `{InstanceName}rl:{policy}:{partitionKey}`. A lease refused carries `Retry-After` from `PTTL` (at least 1 s, rounded up). On a Redis error or an open circuit the request is counted by an in-memory limiter of the same limit (`AttemptAcquire` also uses it, since it cannot await).

- [ ] **Step 1: Write the failing tests.** Unit: `InMemory_factory_allows_the_limit_then_refuses_with_retry_after`; `Policies_ask_the_factory_for_their_limiter` (substitute factory; the three policy names reach it with their limits). Integration, Valkey, two hosts: `Two_hosts_share_one_count_and_the_next_request_on_either_gets_429_with_Retry_After_of_at_least_one` (limit 3); `Window_rolls_over_after_the_window` (1 s window); `A_key_that_lost_its_expiry_is_repaired` (set the key without TTL directly, then acquire: TTL now set); `Policies_do_not_share_counters` (`auth-strict` vs `global` for one address); `IPv6_and_user_partition_keys_work` (`ip:::1`, `user:{sub}`); `Valkey_stopped_each_host_limits_in_memory_and_requests_are_not_slowed_beyond_the_command_timeout`; the existing `RateLimitOrderTests` and `AuthStrictRoutesTests` pass unchanged.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~FixedWindowLimiterFactory|FullyQualifiedName~RedisRateLimit"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Evaluate the script with `ScriptEvaluate`/`LuaScript` (Valkey is Redis-protocol compatible); record every failure and success on `RedisAvailability`.
- [ ] **Step 4: Run** the filter, `dotnet test --no-build -c Release`, and confirm the OpenAPI snapshot test is unchanged. Expected: green.
- [ ] **Step 5: Docs:** `docs/building-blocks/web-common.md` (the factory), `platform-redis.md` (the limiter, the fallback and its `instances x limit` cost).
- [ ] **Step 6: Commit** `feat(web): share rate limits across instances through Redis`.

### Task 11: The SignalR backplane and the shared ticket limit

**Files:**
- Modify: `Platform.Redis/PlatformRedisServiceCollectionExtensions.cs` (`AddSignalR().AddStackExchangeRedis(...)` on the shared multiplexer, channel prefix `{InstanceName}signalr`); `Notifications/Infrastructure/Hubs/SignalRInAppNotificationPusher.cs` only if a backplane failure can escape it
- Test: `tests/TemplateName.IntegrationTests/Redis/RedisBackplaneTests.cs`, using `NotificationHubClient`

**Produces:** with `Caching:Provider = redis`, a push made on one host reaches a client connected to another; the per-user ticket count already shares through Task 10; with Redis down delivery still succeeds (the push is best effort, ADR 0021).

- [ ] **Step 1: Write the failing tests:** `A_push_made_on_host_A_reaches_a_client_connected_to_host_B`; `The_ticket_limit_is_shared_across_hosts` (`Notifications:Hub:TicketsPerMinute` = 2: one ticket from each host, the third request gets 429); `Valkey_stopped_a_notification_still_delivers_and_its_inbox_row_exists` (delivery `Sent`, in-app row present, no exception from the pusher, a warning logged); `Without_Redis_the_hub_works_as_before` (existing `HubTests` unchanged).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RedisBackplane"`. Expected: FAIL.
- [ ] **Step 3: Implement.** If the pusher can throw when the backplane is down, catch and log there (best effort), nowhere else.
- [ ] **Step 4: Run** the filter and `FullyQualifiedName~Hub`. Expected: green.
- [ ] **Step 5: Docs:** `docs/modules/notifications.md` (the "live pushes are per instance" limit becomes: shared with Redis, per instance without), `platform-redis.md` (backplane).
- [ ] **Step 6: Commit** `feat(notifications): push across instances through the Redis backplane`.

### Task 12: Documentation, roadmap and the "Plan 5" sweep

**Files:**
- Modify: `docs/services/api.md` (`Caching`, `Jobs`, `Retention`, `Resilience`, `ConnectionStrings:Redis`), `docs/architecture/overview.md`, `docs/README.md`, `README.md`, `build/template-content/README.md` (and the `CLAUDE.md` copy if it lists building blocks), `docs/blueprint-review.md` Section 6 (row 5 done; new row 5b Audit; two rows for the next plans: social login with phone-number OTP login, and push with SMS channels, each noting the per-provider `Enabled` toggles, unnumbered), `docs/adr/0026` to `0029` (check their status and links)
- Test: `tests/TemplateName.ArchitectureTests/DocumentationTests.cs` (existing checks cover ADR links and config keys)

- [ ] **Step 1:** `grep -rn "Plan 5" docs src README.md build/template-content` and resolve every hit: done work is rewritten as fact, work that moved ("Plan 5b", later plans) is named correctly, nothing keeps promising what now exists.
- [ ] **Step 2:** Run the documentation and architecture tests. Expected: green; fix any config key or ADR link they flag.
- [ ] **Step 3: Commit** `docs: document the platform services and update the roadmap`.

### Task 13: Verification and the template smoke test

- [ ] **Step 1:** `dotnet build -c Release`, `dotnet test --no-build -c Release` (whole suite, Docker running), `dotnet test -c Release --coverage` (the repo's gate; add tests, never lower it), `dotnet format --verify-no-changes`, and the licence command. Expected: all green.
- [ ] **Step 2:** `bash build/scripts/template-smoke.sh`. Expected: the generated project builds with `Platform.Redis` included and its unit and architecture tests pass; no `TemplateName` left in names or content.
- [ ] **Step 3: Manual check.** `docker compose --profile redis up -d`; run two API instances on different ports with `Caching__Provider=redis` and `ConnectionStrings__Redis=localhost:6379`; call `/health/ready` on both (healthy), exceed `auth-strict` across both (one shared 429), stop Valkey (`/health/ready` is Degraded, requests still served), start it again. Write the result in the PR description; do not leave the stack up.
- [ ] **Step 4:** Push `feature/platform-services` and open one PR titled `feat(platform): add jobs, retention, Redis and resilience`. Run the whole-branch review before merge. Then start the Plan 5b (Audit) spec.

---

## Part C: after this plan

- **Plan 5b: Audit module** (own spec: the cross-module change-record contract, the overlap with `auth.AuthAuditLogs`, the archive target for audit logs).
- **Next plans, in this order:** social login (Google, Apple, Microsoft, Facebook and a generic OpenID Connect entry) then phone-number OTP login; push (FCM/APNs) and SMS channels. Every provider and channel is off by default and switched by `Enabled` in configuration. Phone OTP depends on the SMS channel.
- **Backlog from this spec:** Hangfire as an engine behind `IRecurringJob`, an admin jobs API, pub/sub L1 invalidation, archiving instead of deleting, `IDistributedLock`, output caching.

## Spec coverage

| Spec | Task |
|---|---|
| 3.1 scheduler, D2 | 3 |
| 3.2 retention, D3, inbox rule | 4-7 |
| 3.3 Redis, D4-D6 | 9-11 |
| 3.4 resilience, D7 | 8 |
| 3.5 migrations | 2 |
| 4 failure modes | Review Focus 1-5, Tasks 3, 9-11 |
| 5 testing, template smoke | each task, 13 |
| 6 docs, ADRs 0026-0029, packages, scope | 1, 4, 8, 9, 12 |
| D1, D8, 7 out of scope | Part C |
