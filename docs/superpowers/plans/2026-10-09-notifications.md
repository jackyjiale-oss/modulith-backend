# Modulith Backend: Action Plan (Plan 3: Notifications)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every message the template sends goes through one Notifications module: Auth publishes integration events through a new `TemplateName.Modules.Auth.Contracts` project, Notifications consumes them idempotently through its inbox, renders localized Scriban templates in the **recipient's** language and delivers them by email (SMTP, Mailpit in development) and in-app (stored rows plus a SignalR push), with retries, dead-letter, preferences and quiet hours.

**Architecture:** One new module, `TemplateName.Modules.Notifications` (schema `notify`, own `NotificationsDbContext`, inbox, no outbox), plus `TemplateName.Modules.Auth.Contracts` (integration events and one directory interface, nothing else). The Auth outbox dispatcher runs small Auth handlers that turn domain events into integration events and hand them to an in-process publisher; the Notifications consumers record each message in `notify.InboxMessages` and write the notification and its deliveries in the same save. A hosted delivery worker claims due deliveries under a lease, renders them in memory (single-use links are decrypted only there), sends them and records the outcome. Auth's `IEmailSender`, `AuthEmails` and the two email handlers are removed; the SMTP sender moves into the Notifications email channel.

**Tech Stack:** Plan 2 stack plus `Scriban` (templates, BSD-2-Clause), ASP.NET Core SignalR (shared framework, no package), `Microsoft.AspNetCore.SignalR.Client` (integration tests only, MIT). MailKit and Mailpit stay.

**Spec:** `BACKEND_TEMPLATE_BLUEPRINT.md` Sections 7.8, 10, 11.1, 11.3, 12.2 (Notifications row) and Phase 4, as amended by `docs/blueprint-review.md` Sections 2.1, 2.2, 2.6, 2.8, 2.10, 4, 6 (row 3), 7, 8 and 11; the review wins where they disagree. Plan 1 (`docs/superpowers/plans/2026-10-06-foundation-and-core-baseline.md`) and Plan 2 (`docs/superpowers/plans/2026-10-08-auth-core.md`, Part D) are the starting point; Plan 2's PR is not merged yet, so this branch starts from `feature/auth-core` at `0ada17a`.

## Global Constraints

Everything in Plan 1's and Plan 2's Global Constraints applies: Central Package Management with the latest **stable** version, lock files and NuGet Audit; allowed licences `MIT`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause` only, and each new package's terms read for usage fees; Conventional Commits with a header of at most 72 characters; `TimeProvider` only (no `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`); `Async` suffix and `CancellationToken cancellationToken` last; error codes `module.snake_case` with a message in `en`, `ms` and `zh-Hans` `.resx`; docs updated in the same commit; cursor pagination for every list endpoint (`pageSize` default 20 max 100, `cursor`, `sort`, `includeTotalCount`); module types `internal` except the module entry point; `sealed` classes, `record` for messages and DTOs; options `{Section}Options` with `.ValidateDataAnnotations().ValidateOnStart()`; no secrets in files; Shouldly; integration tests non-parallel and `TestContext.Current.CancellationToken`; warnings are errors in Release; never edit an applied migration; never `--no-verify`. Plan-specific values:

- **Workflow:** one branch `feature/notifications`, **one commit per task**, one PR at the end (after Plan 2's PR merges, rebase onto `main`). Never push to `main`. Agent commits end with `Co-Authored-By: <the model that wrote it> <noreply@anthropic.com>`.
- **Module boundary:** Notifications references `TemplateName.Modules.Auth.Contracts` only, never `TemplateName.Modules.Auth`, and never reads an `auth.*` table. Auth references nothing of Notifications. Contracts projects hold only public sealed records, enums and interfaces, and reference only `TemplateName.SharedKernel`.
- **Error codes:** prefix `notifications.`; permission codes `notifications.{resource}.{action}`; routes `/api/v1/notifications/...` and `/api/v1/admin/notifications/...`; the SignalR hub at `/hubs/notifications`.
- **Database:** schema `notify`; tables PascalCase plural; `Guid` keys from `SequentialGuid.Create(now)`; `datetime2(3)` UTC; Dapper reads write their own filters (owner, status); none of the `notify` tables is soft-deleted (they are logs and per-user settings), so there is no `IsDeleted` filter to forget.
- **Secrets:** a single-use link exists in plaintext only in memory, during rendering in the delivery worker. It is never stored in `notify.*` (only Data Protection ciphertext), never in an email subject or in-app text, never in a log, metric tag, `LastError` or exception message. Email addresses are never logged.
- **i18n (review 8):** notifications render with the **recipient's** saved `Locale`, resolved to a supported UI culture (`en`, `ms`, `zh-Hans`; parent chain `zh-CN` → `zh-Hans`, `ms-MY` → `ms`), else `en`; a template missing in that culture falls back to `en`. The request culture and the OS culture are never used. Time zones are IANA ids; an invalid or empty one means `UTC`.
- **Delivery retries (blueprint 10.4):** after failed attempts 1-5 the next attempt is due 1 min, 5 min, 15 min, 1 h, 6 h later; `MaxAttempts` 5; then `DeadLettered`. A permanent failure (rejected recipient, malformed address, render failure) dead-letters at once. A delivery whose `ExpiresAt` has passed is marked `Expired` and not sent.
- **Mandatory security notices:** every `auth.*` type is category `Security`, priority `Critical`, ignores quiet hours, and its email channel cannot be disabled.

## Decisions this plan takes

The human is not available, so these are decided with the spec as the authority. Each has a reason and the cost if it turns out wrong.

| # | Decision | Why | Cost if wrong |
|---|---|---|---|
| D1 | **Two new projects:** `TemplateName.Modules.Notifications` and `TemplateName.Modules.Auth.Contracts`. **No** `TemplateName.Modules.Notifications.Contracts` yet. | Rule D1 of the review: Contracts exist only when another module consumes something. Auth exposes events and a directory; nobody consumes anything from Notifications (Auth's types are declared inside Notifications, D7). | A module that wants to declare its own notification types later moves `INotificationTypeSource` and `NotificationTypeDefinition` into a new Notifications.Contracts project: a mechanical move. |
| D2 | **In-process integration events:** an Auth outbox handler per domain event builds the public integration event and calls `IIntegrationEventPublisher`, which runs every `IIntegrationEventHandler<T>` in its own scope. The event `Id` is the Auth outbox message id (exposed by a new scoped `IOutboxMessageContext`), so a retry republishes the same id. No serialization and no version field: integration events never rest anywhere; a breaking change is a new record type (`...V2IntegrationEvent`). ADR 0018. | Review 2.1/2.2: per-module outbox, consumer idempotency through an inbox. A broker is backlog; the outbox already gives durability and retries. A stable id is what makes the inbox work. | Adding RabbitMQ later adds an envelope (type, version) and a serializer behind the same publisher interface. |
| D3 | **Inbox = per-consumer idempotency record** (`notify.InboxMessages`, PK `(MessageId, Consumer)`, blueprint 11.4 shape), written in the **same save** as the notification rows. A consumer only writes rows; it never sends, so it is fast and safe to retry. | Exactly-once effect per consumer without a second polling loop. Sending is the delivery worker's job with its own retries. | If a consumer ever needs slow work, it must enqueue rather than do it inline (documented rule). |
| D4 | **Single-use links cross the module boundary encrypted.** The Data Protection protector moves from Auth into the building blocks (`Application.Common.Security.ISecretProtector`, purpose `TemplateName.Secrets.v1`). Auth's publisher decrypts the token, builds the link with `Auth:Links` (Auth keeps owning link formats), and publishes it re-protected as `ProtectedActionUrl`. Notifications stores that ciphertext unchanged in `Notifications.ProtectedData` and decrypts it only while rendering an email. Subjects and in-app parts may not reference secret variables (template test). ADR 0020 (extends 0017). | Neither schema ever holds a working link at rest; Notifications needs no knowledge of Auth's token format; one key ring (still `auth.DataProtectionKeys`) serves both. Re-protecting with Notifications' own purpose would add a step without reducing exposure. | Purpose changed from `TemplateName.Auth.Secrets.v1`: protected values pending in a developer database before this change can no longer be read (Plan 2 is unreleased, so no production data). |
| D5 | **Recipient data comes from Auth through a Contracts interface**, `IUserContactDirectory.FindAsync(userId)`, implemented by Auth with Dapper on its own tables, called once when the notification is created (consume time). Destination address, culture and display name are **snapshotted** on the notification and delivery rows. Link events also carry the address the link was issued for. | The outbox does not order messages (ADR 0007), so a local projection fed by events could miss a user; a synchronous directory inside the monolith is always current. Snapshots make retries independent of Auth. | Extracting Notifications into a service turns the directory into an HTTP call or a projection; the interface stays. |
| D6 | **Which Auth events are published:** `EmailVerificationRequested`, `PasswordResetRequested` (with `PasswordResetReason`: `SelfService`, `CreatedByAdmin`, `ForcedByAdmin`), `RegistrationAttempted`, `PasswordChanged`, `UserLockedOut`, `RefreshTokenReuseDetected`. `UserRegistered` and `EmailConfirmed` are **not** published. The publisher skips a verification code that is no longer pending (superseded or consumed) and a user whose address changed. | Plan 2 Part D's `VerificationCodeIssued` becomes two events with business names; the admin-created set-password mail needs its own wording, hence the reason. Nothing consumes `UserRegistered` (the confirmation mail hangs off the verification link), so publishing it is YAGNI. Skipping stale codes fixes Plan 2's known limit "a replaced link may still be emailed". | Adding an event later is one record and one publisher handler. |
| D7 | **Notification types are code-defined** (`INotificationTypeSource`, validated at start into a singleton `NotificationCatalog`); **no `notify.NotificationTypes` table**. Auth's types are declared in Notifications (`AuthNotificationTypes`). | No admin type or template editing in this plan, so a table would be a second copy of the code with nothing reading it. Dependencies stay one-way (Notifications → Auth.Contracts). | An admin type screen later adds a synced table, like `auth.Permissions` (additive migration). |
| D8 | **Templates are Scriban files embedded in the assembly that declares the type**, one file per type × channel × culture × part (`subject`, `text`, `html` for email; `title`, `body` for in-app), plus an email layout per culture. Rendering uses the snapshotted culture, `StrictVariables`, loop and recursion limits, invariant formatting, and **HTML-encodes every model value for `html` parts** (templates cannot forget). Completeness tests enforce the full matrix and identical placeholders across cultures. ADR 0019. | Blueprint 10.4 (Scriban, per type × channel × language, shared layout). Embedded files are reviewed in pull requests, versioned by git and need no seeding; review 8 I9 asks for completeness in CI. | DB-stored, editable templates (blueprint "versioned", admin editor) come later behind the same `INotificationTemplateStore`. |
| D9 | **Data model** (schema `notify`): `Notifications`, `Deliveries`, `InAppNotifications`, `UserPreferences`, `UserSettings` (quiet hours), `HubTickets`, `InboxMessages`. No push devices, webhooks, suppression list or templates table. | The minimum the two channels, preferences, quiet hours and the ticket need. | Each deferred table is additive. |
| D10 | **Preferences are per type × channel, stored only when they differ from the default**; a mandatory channel cannot be disabled (400 `notifications.channel_mandatory`). `auth.password_changed` and `auth.token_reuse_detected` have a mandatory email channel and an **optional** in-app channel, so the preferences API has real content. No global mute, no digest. | Blueprint 10.4 per type and channel; security alerts cannot be opted out of (10.6). | Global mute and digest are additive columns. |
| D11 | **Quiet hours** are a daily local window (`UserSettings.QuietHoursStart`/`End`, `time`), interpreted in the recipient's **Auth time zone** (IANA, from the directory; invalid or empty means `UTC`, with a warning log naming the user id). They defer **email** deliveries of non-`Critical` types to the window's end; in-app rows are never deferred (they disturb no one). `Critical` ignores them. Evaluated once, when the delivery is created. | One time zone per user (set through `PUT /api/v1/auth/me`), no drift between modules. Blueprint 10.4: critical bypasses. | A user who changes time zone after a deferred delivery was created keeps the old deferral (documented). |
| D12 | **SignalR authentication by single-use hub tickets.** `POST /api/v1/notifications/hub-tickets` (bearer) returns a 32-byte random ticket valid 30 s, stored only as a SHA-256 hash in `notify.HubTickets` and claimed with one conditional `UPDATE`. A `HubTicket` authentication scheme accepts it **only** on `/hubs/notifications`, from the `access_token` query parameter (where SignalR clients put the `accessTokenFactory` value for WebSockets) or the `Authorization: Bearer` header (where they put it for negotiate); the client's factory fetches a fresh ticket for each call, and the hub allows the **WebSockets transport only** (long polling and SSE would need a reusable credential on every poll). JWTs are **never** read from a query string. The connection closes when its authentication expires (`Notifications:Hub:ConnectionLifetime`, 10 min) and the client reconnects with a new ticket. The query string is kept out of request logs and traces (test). ADR 0021. | Review 2.10: the JWT would land in logs. A ticket in a log is spent and expired within 30 s. A table (not memory) lets negotiate and connect hit different instances. | Clients must fetch a ticket per connect; documented with a JS example in the module doc. |
| D13 | **SignalR scale-out (Redis backplane) is out of scope** (Plan 5). One instance, or sticky sessions without a backplane, pushes only to connections on the instance that delivered; the inbox API stays the source of truth. | Plan 5 owns Redis. | Until then a multi-instance deployment misses some live pushes (documented); nothing is lost. |
| D14 | **Delivery worker is a hosted `BackgroundService`** (`DeliveryDispatcher` + `DeliveryBackgroundService`) with the outbox's claim pattern: `UPDLOCK, READPAST, ROWLOCK` batch claim under a lease, every final update guarded by the lease it claimed. Delivery is at least once (a crash after SMTP accepted re-sends). In-app delivery inserts the in-app row with the delivery id as key in the same save as `Sent`, so it is exactly once. Hangfire is Plan 5. | Proven pattern from Plan 1 Task 9; no new dependency. | Plan 5 may move polling to Hangfire behind the same dispatcher. |
| D15 | **SMTP settings move** from `Auth:Email:*` to `Notifications:Email:*` (same keys and defaults). The host refuses to start while any `Auth:Email:*` key is still set, with a message that names the new section. `Auth:Links:*` and `Auth:Verification:*` stay in Auth. | Auth no longer sends mail; a silently ignored old setting would send production mail to `localhost`. | One rename for anyone who configured Plan 2. |
| D16 | **Admin tooling is limited to deliveries:** list (cursor, filters) and retry of a dead-lettered delivery. Permissions `notifications.delivery.view` and `notifications.delivery.retry`, declared by `NotificationsPermissionSource`. `PermissionDefinition` gains `bool AdminDefault = false`: the Auth seeder grants a flagged code to `Admin` in the run that **first inserts** it (Auth's own defaults move to the flag). `SuperAdmin` keeps getting everything. Destinations are masked in admin responses. | Blueprint 10.5 admin list trimmed to what this plan delivers; the flag is how a later module gives Admin a default without editing Auth. | Template editor, test send, suppression list are later. |
| D17 | **No scheduling, cancellation, digest, push, SMS, webhooks, bulk sends, provider callbacks, attachments or fallback channels** in this plan (review 6 row 3 scope). | Scope of row 3. | Part C lists them. |

## Review Focus

Inputs the spec implies and a person will meet, most likely first. Each is pinned by a named test in the owning task.

1. **The same integration event arrives twice** (the Auth outbox retries a handler after Notifications already committed, or two dispatchers race past an expired lease): one notification, one delivery per channel, one email. Owned by Task 10 (`Same_event_consumed_twice_creates_one_notification`, `Concurrent_duplicate_consumption_creates_one_notification`) and Task 17 (`Auth_outbox_retry_sends_one_email`).
2. **A recipient whose `Locale` is null, malformed or unsupported** (`null`, `""`, `"!!"`, `fr-FR`) or regional (`zh-CN`, `ms-MY`): rendered in the right supported culture or English, never in the triggering request's language or the OS culture. Owned by Task 5 (`Resolve_maps_locales_to_supported_cultures`), Task 8 (`Rendering_ignores_current_ui_culture`) and Task 10 (`Unsupported_locale_is_snapshotted_as_en`).
3. **An address the SMTP server rejects permanently, or a malformed address:** dead-lettered after one attempt, no retry loop, and neither the address nor the link appears in a log, `LastError` or metric. A transient failure is retried on the schedule and dead-lettered after five attempts. Owned by Task 12 (`Rejected_recipient_is_dead_lettered_without_retry_and_logs_no_address`) and Task 11 (`Transient_failures_follow_the_schedule_and_dead_letter_after_five`).
4. **Two worker instances** (or two threads) polling the same table: every delivery is sent exactly once. Owned by Task 11 (`Concurrent_dispatchers_send_each_delivery_once`).
5. **Quiet-hours edge cases:** a window across midnight, a DST transition, an invalid or empty time zone, and a mandatory security notice inside the window (sent at once). Owned by Task 6 (`NextAllowedAt_*`) and Task 10 (`Security_notice_ignores_quiet_hours`, `Invalid_time_zone_is_treated_as_utc`).

Also pinned, outside the five: a template with a missing placeholder (Task 8), an expired or reused hub ticket (Task 15), paging stability with equal timestamps and a large unread count (Task 13), an expired reset link waiting in a retry (Task 11).

---

## Part A: Owner actions

- [ ] **A1: Read D1-D17.** They are decided; say so before Task 1 if one is wrong.
- [ ] **A2: Docker Desktop stays running** (SQL Server and Mailpit containers).
- [ ] **A3: Merge Plan 2's PR first**, then rebase `feature/notifications` onto `main` before opening this plan's PR.
- [ ] **A4: Choose the execution method:** Subagent-driven (recommended: tasks share many interfaces, and Tasks 4, 10-12 and 15 are security-sensitive) or Native.
- [ ] **A5: Anyone who set `Auth:Email:*` in user secrets or an environment** renames the keys to `Notifications:Email:*` (D15) after Task 12.

---

## File Structure

```
src/
├─ BuildingBlocks/
│  ├─ TemplateName.SharedKernel/IIntegrationEvent.cs                                                         Task 1
│  ├─ TemplateName.Application.Common/Messaging/{IIntegrationEventHandler, IIntegrationEventPublisher,
│  │                                             IOutboxMessageContext}.cs                                   Task 1
│  ├─ TemplateName.Application.Common/Security/ISecretProtector.cs                                          Task 2
│  ├─ TemplateName.Infrastructure.Common/Messaging/{InProcessIntegrationEventPublisher, OutboxMessageContext}.cs
│  ├─ TemplateName.Infrastructure.Common/Inbox/{InboxMessage, InboxModelBuilderExtensions, Inbox}.cs         Task 1
│  └─ TemplateName.Infrastructure.Common/Security/DataProtectionSecretProtector.cs                           Task 2
├─ Modules/Auth/
│  ├─ TemplateName.Modules.Auth.Contracts/{IntegrationEvents/*.cs, Users/{IUserContactDirectory, UserContact}.cs}   Task 3
│  └─ TemplateName.Modules.Auth/Application/IntegrationEvents/Publish*DomainEventHandler.cs,
│     Infrastructure/Contracts/UserContactDirectory.cs                                                       Tasks 3-4
│     (removed in Task 12: IEmailSender, AuthEmails, Send*EmailDomainEventHandler, Infrastructure/Email/*)
├─ Modules/Notifications/TemplateName.Modules.Notifications/
│  ├─ Domain/{Notifications,Deliveries,InApp,Preferences,HubTickets}/                                        Task 6
│  ├─ Application/Abstractions/                    repositories, IInbox, IUnitOfWork, channels, renderer, IInAppNotificationPusher
│  ├─ Application/Catalog/                         NotificationTypeDefinition, INotificationTypeSource, NotificationCatalog,
│  │                                               RecipientCulture, AuthNotificationTypes                    Tasks 5, 9
│  ├─ Application/Scheduling/                      NotificationRequest, NotificationScheduler                Task 10
│  ├─ Application/AuthEvents/                      one IIntegrationEventHandler per Auth event               Task 10
│  ├─ Application/{Inbox,Preferences,HubTickets,Admin/Deliveries}/{UseCase}/                                 Tasks 13-16
│  ├─ Infrastructure/{Persistence,Templates,Delivery,Channels/Email,Channels/InApp,Hub,Observability}/       Tasks 7-15
│  ├─ Templates/{Email,InApp}/*.scriban            embedded resources                                        Tasks 8-9
│  ├─ Endpoints/                                   NotificationEndpoints, AdminDeliveryEndpoints, requests
│  ├─ Resources/NotificationsErrorMessages(.ms|.zh-Hans).resx
│  └─ NotificationsModule.cs                       AddNotificationsModule / MapNotificationsEndpoints / MapNotificationsHub
docs/adr/0018…0021-*.md, docs/modules/notifications.md, docs/modules/auth.md, docs/building-blocks/*.md, docs/services/api.md,
docs/architecture/overview.md, docs/README.md, README.md, build/template-content/README.md
tests/{UnitTests/Notifications, IntegrationTests/Notifications, IntegrationTests/Infrastructure/*}, ArchitectureTests (Contracts rules)
```

No change to `.template.config/template.json` is needed (it copies `src/` whole); every new project goes into `TemplateName.slnx`, and `bash build/scripts/template-smoke.sh` proves the generated project builds.

## Part B: Implementation tasks

| # | Task | Needs | Commit header |
|---|---|---|---|
| 1 | Integration events, publisher and inbox building blocks | Plan 2 | `feat(messaging): add integration events, publisher and inbox` |
| 2 | Shared secret protector | 1 | `refactor(auth): move the secret protector to the building blocks` |
| 3 | Auth contracts and user contact directory | 1 | `feat(auth): add auth contracts with events and contact directory` |
| 4 | Auth publishes integration events | 2, 3 | `feat(auth): publish auth integration events from the outbox` |
| 5 | Notifications skeleton, catalog and recipient culture | 3 | `chore(notifications): add the notifications module skeleton` |
| 6 | Domain | 5 | `feat(notifications): add notification, delivery and preference domain` |
| 7 | Persistence | 6 | `feat(notifications): add notifications persistence and migration` |
| 8 | Template engine | 5 | `feat(notifications): add scriban template rendering` |
| 9 | Auth notification types and templates | 8 | `feat(notifications): add auth security notification templates` |
| 10 | Scheduler and Auth event consumers | 4, 7, 9 | `feat(notifications): consume auth events through the inbox` |
| 11 | Delivery worker and in-app channel | 10 | `feat(notifications): add delivery worker with retries and dead-letter` |
| 12 | Email channel and cut-over from Auth | 11 | `feat(notifications)!: deliver auth emails through notifications` |
| 13 | In-app inbox API | 11 | `feat(notifications): add in-app inbox endpoints` |
| 14 | Preferences and quiet hours API | 10 | `feat(notifications): add preferences and quiet hours endpoints` |
| 15 | SignalR hub with tickets | 13 | `feat(notifications): add signalr hub with single-use tickets` |
| 16 | Admin deliveries and permissions | 12 | `feat(notifications): add admin delivery list and retry` |
| 17 | End-to-end flow, docs, snapshot, smoke | 1-16 | `docs(notifications): document the module and verify the full flow` |

### Task 1: Integration events, publisher and inbox building blocks

`Model: opus (concurrency and idempotency)`

**Files:**
- Create: `src/BuildingBlocks/TemplateName.SharedKernel/IIntegrationEvent.cs`; `TemplateName.Application.Common/Messaging/{IIntegrationEventHandler.cs, IIntegrationEventPublisher.cs, IOutboxMessageContext.cs}`; `TemplateName.Infrastructure.Common/Messaging/{InProcessIntegrationEventPublisher.cs, OutboxMessageContext.cs}`; `TemplateName.Infrastructure.Common/Inbox/{InboxMessage.cs, InboxModelBuilderExtensions.cs, Inbox.cs}`; `docs/adr/0018-in-process-integration-events-with-inbox.md`
- Modify: `Messaging/MessagingServiceCollectionExtensions.cs` (scan `IIntegrationEventHandler<>`), `Outbox/OutboxDispatcher.cs` (set the message context; return `OccurredAt` from the claim), `Outbox/OutboxServiceCollectionExtensions.cs`, `InfrastructureServiceCollectionExtensions.cs` (register the publisher and the scoped context), `tests/TemplateName.ArchitectureTests/Assemblies.cs` (`HandlerInterfaces` gains `IIntegrationEventHandler<>`), `tests/TemplateName.IntegrationTests/Persistence/TestDbContext.cs` (`ApplyInbox()`), `docs/building-blocks/{shared-kernel,application-common,infrastructure-common}.md`, `docs/README.md`, `docs/architecture/overview.md` (ADR list)
- Test: `tests/TemplateName.UnitTests/Application/InProcessIntegrationEventPublisherTests.cs`, `tests/TemplateName.IntegrationTests/Outbox/OutboxMessageContextTests.cs`, `tests/TemplateName.IntegrationTests/Inbox/InboxTests.cs`

**Interfaces:**
- Consumes: `OutboxDispatcher<TContext>`, `AddApplicationHandlers`, `SequentialGuid`, `TimeProvider`, `TestDbContext`.
- Produces:
  - `public interface IIntegrationEvent { Guid Id { get; } DateTimeOffset OccurredAt { get; } }` (SharedKernel). Implementations are `public sealed record {Noun}{PastTense}IntegrationEvent` in a `*.Contracts` project.
  - `public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent { Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken); }`
  - `public interface IIntegrationEventPublisher { Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken) where TEvent : IIntegrationEvent; }`. `InProcessIntegrationEventPublisher` (singleton, `IServiceScopeFactory`) runs every registered handler of `TEvent` **sequentially, each in its own async scope**; every handler runs even if an earlier one threw; afterwards one failure is rethrown as is, several as an `AggregateException`. Cancellation propagates immediately.
  - `public interface IOutboxMessageContext { Guid MessageId { get; } DateTimeOffset OccurredAt { get; } }`; `OutboxMessageContext` (scoped) throws `InvalidOperationException` when read outside an outbox dispatch. `OutboxDispatcher` sets it in each message's scope before running the handlers (claim `OUTPUT` adds `inserted.OccurredAt`).
  - `public sealed class InboxMessage { Guid MessageId; string Consumer (max 500); DateTime ProcessedAt; }`, table `InboxMessages`, PK `(MessageId, Consumer)`; `public static ModelBuilder ApplyInbox(this ModelBuilder modelBuilder)` maps it into the context's default schema.
  - `public sealed class Inbox<TContext>(TContext context, TimeProvider timeProvider) where TContext : DbContext` with `Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken)`, `void Record(Guid messageId, string consumer)` (stages the row; the caller's `SaveChangesAsync` commits it with its own changes) and `static bool IsDuplicate(DbUpdateException exception)` (SQL error 2627 or 2601 on the inbox key). Registered by `services.AddInbox<TContext>()` (scoped).
- Rule (ADR 0018, written in this task): the integration event `Id` equals `IOutboxMessageContext.MessageId` of the domain event it came from; one domain event maps to at most one integration event.

- [ ] **Step 1: Write the failing unit tests** (`InProcessIntegrationEventPublisherTests`, a real `ServiceCollection`): `Publishes_to_every_handler_in_its_own_scope` (two handlers record the scoped object they received; two distinct instances); `Runs_every_handler_even_when_one_throws_and_rethrows_the_failure`; `Two_failures_are_rethrown_as_aggregate`; `No_handler_is_a_no_op`; `Cancellation_is_not_wrapped`.
- [ ] **Step 2: Write the failing integration tests:** `OutboxMessageContextTests.Handler_sees_the_outbox_message_id_and_the_same_id_on_retry` (a test domain handler records `IOutboxMessageContext.MessageId`; `FlakySwitch.FailNext`; two dispatches; both recorded ids equal the `OutboxMessages.Id`); `Reading_the_context_outside_a_dispatch_throws`. `InboxTests` (on `TestDbContext`): `Record_then_save_marks_processed`; `Second_record_of_the_same_message_and_consumer_is_a_duplicate` (`IsDuplicate` true on the `DbUpdateException`); `Same_message_for_another_consumer_is_not_a_duplicate`.
- [ ] **Step 3: Run** `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~InProcessIntegrationEventPublisher"` and the integration filters `~OutboxMessageContext|~InboxTests`. Expected: FAIL (types missing).
- [ ] **Step 4: Implement** per Interfaces. Write ADR 0018 (context: review 2.1/2.2; options: direct module calls, in-process events + inbox, broker; decision D2/D3; consequences: at-least-once publish, exactly-once effect per consumer, no ordering, publishing consumers must be fast and local). Document the new types in the three building-block pages and list ADR 0018 in `docs/README.md` and the overview.
- [ ] **Step 5: Run** unit, architecture and integration tests; expected PASS (the existing `Concurrent_dispatchers_process_each_message_once` stays green).
- [ ] **Step 6: Commit** `feat(messaging): add integration events, publisher and inbox`.

### Task 2: Shared secret protector

`Model: sonnet`

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Security/ISecretProtector.cs`, `src/BuildingBlocks/TemplateName.Infrastructure.Common/Security/DataProtectionSecretProtector.cs`
- Delete: `src/Modules/Auth/.../Application/Abstractions/ISecretProtector.cs`, `.../Infrastructure/Security/DataProtectionSecretProtector.cs`
- Modify: `InfrastructureServiceCollectionExtensions.cs` (`AddDataProtection()` without persistence plus the protector singleton), `AuthModule.cs` (drops its registration; keeps `SetApplicationName` and `PersistKeysToDbContext<AuthDbContext>`), every Auth file that used the old interface (`using` change only), `tests/TemplateName.UnitTests/Auth/SecretProtectorTests.cs` → `tests/TemplateName.UnitTests/Infrastructure/SecretProtectorTests.cs`, `docs/modules/auth.md` (Security services), `docs/building-blocks/{application-common,infrastructure-common}.md`

**Interfaces:**
- Produces: `public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string protectedValue); }` (namespace `TemplateName.Application.Common.Security`; `Unprotect` throws `CryptographicException` on a changed value). `DataProtectionSecretProtector` uses purpose **`TemplateName.Secrets.v1`** (D4).

- [ ] **Step 1: Move the tests** and add `Purpose_is_TemplateName_Secrets_v1` (a value protected by a provider-created protector with that purpose unprotects through the service). **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Move the code;** Auth behaviour is unchanged apart from the purpose (note D4's consequence in the auth doc).
- [ ] **Step 4: Run** all suites; expected PASS (`Outbox_message_does_not_contain_the_plaintext_token` still green). **Commit** `refactor(auth): move the secret protector to the building blocks`.

### Task 3: Auth contracts and user contact directory

`Model: sonnet`

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/TemplateName.Modules.Auth.Contracts.csproj` (references `TemplateName.SharedKernel` only), `IntegrationEvents/{EmailVerificationRequestedIntegrationEvent, PasswordResetRequestedIntegrationEvent, PasswordResetReason, RegistrationAttemptedIntegrationEvent, PasswordChangedIntegrationEvent, UserLockedOutIntegrationEvent, RefreshTokenReuseDetectedIntegrationEvent}.cs`, `Users/{IUserContactDirectory, UserContact}.cs`; Auth `Infrastructure/Contracts/UserContactDirectory.cs`
- Create (tests): `tests/TemplateName.ArchitectureTests/ContractsTests.cs`, `tests/TemplateName.IntegrationTests/Auth/UserContactDirectoryTests.cs`
- Modify: `TemplateName.slnx`, `TemplateName.Modules.Auth.csproj` (references the Contracts project), `AuthModule.cs`, `tests/TemplateName.ArchitectureTests/Assemblies.cs` (new `Contracts` list), test csproj references, `docs/modules/auth.md` (Purpose and boundaries, Events), `docs/architecture/overview.md` (module map)

**Interfaces:**
- Produces (namespace `TemplateName.Modules.Auth.Contracts.IntegrationEvents`, every record `public sealed record ... : IIntegrationEvent` with `Guid Id, DateTimeOffset OccurredAt` first):
  - `EmailVerificationRequestedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string Email, string ProtectedActionUrl, DateTimeOffset ExpiresAt)`
  - `PasswordResetRequestedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string Email, string ProtectedActionUrl, DateTimeOffset ExpiresAt, PasswordResetReason Reason)`; `public enum PasswordResetReason { SelfService = 0, CreatedByAdmin = 1, ForcedByAdmin = 2 }`
  - `RegistrationAttemptedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId)`, `PasswordChangedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId)`, `UserLockedOutIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, DateTimeOffset LockoutEnd)`, `RefreshTokenReuseDetectedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, Guid SessionId)`
  - `ProtectedActionUrl` is `ISecretProtector` ciphertext of the full link (D4); `Email` is the address the link was issued for.
- Produces (namespace `TemplateName.Modules.Auth.Contracts.Users`): `public interface IUserContactDirectory { Task<UserContact?> FindAsync(Guid userId, CancellationToken cancellationToken); }`; `public sealed record UserContact(Guid UserId, string Email, string DisplayName, string Locale, string TimeZone)`. `UserContactDirectory` (Auth, scoped) is one Dapper query on `auth.Users` with `IsDeleted = 0` written in the SQL; suspended users are returned (security notices still reach them).

- [ ] **Step 1: Write the failing architecture tests** (`ContractsTests`, over `Assemblies.Contracts`): `Contracts_reference_only_the_shared_kernel`; `Contracts_types_are_public_and_sealed_records_enums_or_interfaces`; `Integration_events_are_named_IntegrationEvent_and_implement_the_interface`; `Contracts_do_not_reference_module_assemblies`. (A module referencing another module's main assembly is already caught by `ModuleBoundaryTests`.)
- [ ] **Step 2: Write the failing integration tests** (`UserContactDirectoryTests`): `Finds_the_contact_with_locale_and_time_zone`; `Soft_deleted_user_is_not_found` (Dapper filter proof: the row exists); `Unknown_user_is_null`; `Suspended_user_is_found`.
- [ ] **Step 3: Run**; expected FAIL. **Step 4: Implement**; register the directory in `AddAuthModule`. Document the contracts in `docs/modules/auth.md` (Events: integration events table, "Contracts" paragraph).
- [ ] **Step 5: Run** all suites and `bash build/scripts/template-smoke.sh`; expected PASS. **Commit** `feat(auth): add auth contracts with events and contact directory`.

### Task 4: Auth publishes integration events

`Model: opus (secrets crossing modules)`

**Files:**
- Create: `src/Modules/Auth/.../Application/IntegrationEvents/{PublishVerificationCodeIssuedDomainEventHandler, PublishRegistrationAttemptedDomainEventHandler, PublishPasswordChangedDomainEventHandler, PublishUserLockedOutDomainEventHandler, PublishRefreshTokenReuseDetectedDomainEventHandler}.cs`, `Domain/Verification/VerificationTrigger.cs`, `docs/adr/0020-secrets-in-cross-module-events.md`
- Modify: `Domain/Verification/Events/VerificationCodeIssuedDomainEvent.cs` (adds `VerificationTrigger Trigger` **last, defaulting to `SelfService`**, so pending rows without it still deserialize), `Domain/Verification/VerificationCode.cs` (`Issue` takes the trigger), `Application/Abstractions/IVerificationCodeRepository.cs` (+`GetByIdAsync`), `Infrastructure/Persistence/VerificationCodeRepository.cs`, `Application/Passwords/PasswordResetLinkIssuer.cs`, its three callers (forgot, admin create, admin force), resx untouched, `docs/modules/auth.md`, `docs/README.md`, `docs/architecture/overview.md`
- Create (tests): `tests/TemplateName.UnitTests/Auth/IntegrationEvents/*PublisherTests.cs` (one file per handler), `tests/TemplateName.IntegrationTests/Infrastructure/RecordingIntegrationEventHandler.cs`, `tests/TemplateName.IntegrationTests/Auth/IntegrationEventPublishingTests.cs`

**Interfaces:**
- Consumes: `IIntegrationEventPublisher`, `IOutboxMessageContext`, `ISecretProtector` (Task 2), Task 3's records, `LinksOptions`, `IUserRepository`.
- Produces: `internal enum VerificationTrigger { SelfService = 0, CreatedByAdmin = 1, ForcedByAdmin = 2 }`; `PasswordResetLinkIssuer.IssueAsync(User user, VerificationTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)`; `IVerificationCodeRepository.GetByIdAsync(Guid id, CancellationToken cancellationToken) : Task<VerificationCode?>`.
- Handler rules (all `internal sealed`, `IDomainEventHandler<T>`; `Id = context.MessageId`, `OccurredAt = context.OccurredAt`):
  - `PublishVerificationCodeIssuedDomainEventHandler`: loads the code by id and the user; publishes **nothing** (Information log with the code id only) when the code is not `IsPending(now)`, the user is gone, or `user.NormalizedEmail != Target`. Otherwise `Unprotect` the token, build the link with `LinksOptions.ConfirmEmailLink`/`ResetPasswordLink`, `Protect` the link, and publish `EmailVerificationRequested` (`EmailVerify`) or `PasswordResetRequested` with `Reason` mapped from the trigger, `Email = user.Email`, `ExpiresAt = code.ExpiresAt`.
  - The other four map one to one (`UserLockedOut` carries `LockoutEnd`; reuse carries `SessionId`).
- The existing `Send*EmailDomainEventHandler`s stay until Task 12, so emails are unchanged in this commit.

- [ ] **Step 1: Write the failing unit tests** (NSubstitute, fixed `IOutboxMessageContext`): `Verification_publishes_email_verification_with_protected_link_and_expiry` (the published `ProtectedActionUrl` unprotects to `ConfirmEmailLink(token)`; it is not the plaintext link); `Reset_maps_each_trigger_to_its_reason` (theory over the three); `Superseded_or_consumed_code_publishes_nothing`; `Changed_address_publishes_nothing`; `Event_id_and_time_come_from_the_outbox_message`; `Nothing_logged_contains_the_token_link_or_address` (a recording logger); one test per one-to-one mapper.
- [ ] **Step 2: Write the failing integration tests** (`IntegrationEventPublishingTests`, the factory registers `RecordingIntegrationEventHandler<T>` for the six events): `Register_then_dispatch_publishes_one_email_verification_event`; `Retried_dispatch_republishes_the_same_id` (`FlakySwitch`-style failing second handler, two dispatches, both ids equal); `Forgot_twice_publishes_only_the_live_link` (the first code is invalidated before dispatch); `Admin_create_publishes_reset_with_CreatedByAdmin`; `Lockout_password_change_and_token_reuse_publish_their_events`.
- [ ] **Step 3: Run**; expected FAIL. **Step 4: Implement.** Write ADR 0020 (context: ADR 0017, review 2.2; decision D4: one purpose in the building blocks, the link re-protected by Auth, stored as ciphertext by Notifications, decrypted only in memory at render; consequence: the key ring in `auth.DataProtectionKeys` now protects Notifications data too and must be encrypted at rest in Plan 6). Update `docs/modules/auth.md` Events (publisher handlers, the stale-code rule that removes the "replaced link may still be emailed" known limit).
- [ ] **Step 5: Run** all suites; expected PASS. **Commit** `feat(auth): publish auth integration events from the outbox`.

### Task 5: Notifications skeleton, catalog and recipient culture

`Model: sonnet`

**Files:**
- Create: `src/Modules/Notifications/TemplateName.Modules.Notifications/TemplateName.Modules.Notifications.csproj` (references like `TemplateName.Modules.Auth.csproj` minus Auth-only packages, plus `TemplateName.Modules.Auth.Contracts`), `NotificationsModule.cs`, `Application/Abstractions/IUnitOfWork.cs`, `Domain/Notifications/{NotificationChannel, NotificationCategory, NotificationPriority}.cs`, `Application/Catalog/{NotificationTypeDefinition, INotificationTypeSource, NotificationCatalog, RecipientCulture, TimeZoneResolver}.cs`, `Resources/NotificationsErrorMessages.cs` with the three empty `.resx`, `docs/modules/notifications.md` (ten sections, `None.` where empty)
- Modify: `TemplateName.slnx`, `src/Host/TemplateName.Api/{TemplateName.Api.csproj, Program.cs}`, `tests/TemplateName.ArchitectureTests/Assemblies.cs` (`Modules`, `ErrorMessageResources`), unit and integration test csproj references, `docs/README.md`, `docs/architecture/overview.md` (module map, `notify` schema ownership)
- Test: `tests/TemplateName.UnitTests/Notifications/{NotificationCatalogTests, RecipientCultureTests, TimeZoneResolverTests}.cs`

**Interfaces:**
- Produces:
  - `public static class NotificationsModule { IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration); IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app); IEndpointRouteBuilder MapNotificationsHub(this IEndpointRouteBuilder app); }` (map methods empty until Tasks 13 and 15). `Program.cs` calls `AddNotificationsModule` after `AddAuthModule`, `api.MapNotificationsEndpoints()` and `app.MapNotificationsHub()`.
  - `internal enum NotificationChannel : byte { Email = 1, InApp = 2 }`, `internal enum NotificationCategory : byte { Security = 1, Account = 2, System = 3 }`, `internal enum NotificationPriority : byte { Low = 1, Normal = 2, High = 3, Critical = 4 }` (values are persisted).
  - `internal sealed record NotificationTypeDefinition(string Code, NotificationCategory Category, NotificationPriority Priority, IReadOnlyList<NotificationChannel> DefaultChannels, IReadOnlyList<NotificationChannel> MandatoryChannels, IReadOnlySet<string> Variables, IReadOnlySet<string> SecretVariables)`; derived `bool IsMandatory(NotificationChannel channel)`, `bool IsConfigurable` (some default channel is not mandatory).
  - `internal interface INotificationTypeSource { IReadOnlyCollection<NotificationTypeDefinition> Types { get; } }`; templates live in the source's own assembly (Task 8).
  - `internal sealed class NotificationCatalog(IEnumerable<INotificationTypeSource> sources)` (singleton): `IReadOnlyCollection<NotificationTypeDefinition> All`, `NotificationTypeDefinition? Find(string code)`, `Assembly TemplateAssemblyOf(string code)`. Validation at first use and on start (an `IHostedService` that touches it): code matches `^[a-z]+\.[a-z_]+\z` and is at most 100 characters; no duplicate code across sources; at least one default channel; mandatory ⊆ default; variables and secret variables match `^[a-z][a-z0-9_]*\z` and do not overlap; failures throw `InvalidOperationException` naming the code and the source type.
  - `internal static class RecipientCulture { static IReadOnlyList<string> Supported = ["en", "ms", "zh-Hans"]; static string Resolve(string? locale); }`: walks `CultureInfo.GetCultureInfo(locale, predefinedOnly: true)` and its parents to the first supported name; null, blank, invalid or unsupported gives `en`. It never reads `CultureInfo.CurrentUICulture`.
  - `internal static class TimeZoneResolver { static (TimeZoneInfo Zone, bool IsFallback) Resolve(string? ianaId); }`: `TimeZoneInfo.TryFindSystemTimeZoneById`, else `UTC` with `IsFallback = true`.

- [ ] **Step 1: Write the failing tests:** catalog (`Valid_sources_build_the_catalog`, `Duplicate_code_across_sources_fails_naming_both`, `Mandatory_channel_outside_defaults_fails`, `Overlapping_secret_variable_fails`, `Bad_code_fails`); `Resolve_maps_locales_to_supported_cultures` (Review Focus 2: theory `null→en`, `""→en`, `"!!"→en`, `"fr-FR"→en`, `"zh-CN"→zh-Hans`, `"zh-SG"→zh-Hans`, `"ms-MY"→ms`, `"ms"→ms`, `"en-GB"→en`, `"zh-Hant"→en`); `Resolve_ignores_current_ui_culture` (set `CurrentUICulture` to `ms` inside the test, `Resolve(null)` is `en`); time zones (`Asia/Kuala_Lumpur` found, `Mars/Olympus`, `""`, null and a Windows id `"Singapore Standard Time"` on Linux fall back to UTC with the flag; accept the Windows id only if the runtime resolves it).
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement** and wire the module (`AddApplicationHandlers`, `AddErrorMessages<NotificationsErrorMessages>`, the catalog and its start-up check). Fill `docs/modules/notifications.md` Purpose and boundaries (D1-D3, D5, D17).
- [ ] **Step 4: Run** `dotnet build -c Release`, all tests, `dotnet format --verify-no-changes`, `bash build/scripts/template-smoke.sh`; expected green. **Commit** `chore(notifications): add the notifications module skeleton`.

### Task 6: Domain

`Model: sonnet`

**Files:**
- Create: `Domain/Notifications/{Notification.cs, NotificationErrors.cs}`, `Domain/Deliveries/{Delivery.cs, DeliveryStatus.cs, DeliveryRetryPolicy.cs, DeliveryErrors.cs}`, `Domain/InApp/{InAppNotification.cs, InAppNotificationErrors.cs}`, `Domain/Preferences/{UserPreference.cs, UserSettings.cs, QuietHours.cs, PreferenceErrors.cs}`, `Domain/HubTickets/HubTicket.cs`
- Modify: `NotificationsErrorMessages` resx (three), `docs/modules/notifications.md` (Domain model with a Mermaid state diagram for `Delivery`, Error codes)
- Test: `tests/TemplateName.UnitTests/Notifications/{NotificationTests, DeliveryTests, DeliveryRetryPolicyTests, QuietHoursTests, UserSettingsTests, HubTicketTests}.cs`

**Interfaces:**
- `internal enum DeliveryStatus : byte { Pending = 0, Sent = 1, DeadLettered = 2, Expired = 3 }`.
- `internal sealed class Notification : AggregateRoot<Guid>` with `TypeCode`, `RecipientUserId`, `Priority`, `Culture`, `Data` (JSON of non-secret variables), `ProtectedData` (`string?`, JSON object of secret variable → `ISecretProtector` ciphertext), `CorrelationId` (`string?`, trace id), `SourceMessageId` (the integration event id), `CreatedAt`, `ExpiresAt` (`DateTime?`), `IReadOnlyCollection<Delivery> Deliveries`. `static Notification Create(string typeCode, NotificationPriority priority, Guid recipientUserId, string culture, string data, string? protectedData, Guid sourceMessageId, string? correlationId, DateTimeOffset? expiresAt, DateTimeOffset now)`; `Delivery AddDelivery(NotificationChannel channel, string? destination, DateTimeOffset firstAttemptAt, DateTimeOffset now)` (a channel at most once).
- `internal sealed class Delivery : Entity<Guid>` with `NotificationId`, `Channel`, `Destination` (email address; null for in-app), `Status`, `AttemptCount`, `NextAttemptAt` (`DateTime?`, null unless `Pending`), `LockedUntil`, `LastError` (max 200, a reason code such as `smtp_550` or `render_failed`, never free text), `RenderedSubject` (max 300), `CreatedAt`, `SentAt`, `DeadLetteredAt`, `ExpiresAt` (copied from the notification). `Result Retry(DateTimeOffset now)`: only from `DeadLettered` (`DeliveryErrors.NotRetryable`, Conflict, `notifications.delivery_not_retryable`) and only while not expired (`DeliveryErrors.Expired`, Conflict, `notifications.delivery_expired`); resets to `Pending`, `AttemptCount = 0`, `NextAttemptAt = now`, clears `LastError` and `DeadLetteredAt`. `DeliveryErrors.NotFound(Guid id)` (`notifications.delivery_not_found`).
- `public static class DeliveryRetryPolicy { public static DateTime? NextAttemptAt(int failedAttempts, DateTime utcNow, int maxAttempts); }`: delays 1 min, 5 min, 15 min, 1 h, 6 h after attempts 1-5 (clamped to 6 h beyond); `null` once `failedAttempts >= maxAttempts`.
- `internal sealed class InAppNotification : Entity<Guid>` (its id **is** the delivery id): `UserId`, `NotificationId`, `TypeCode`, `Category`, `Title` (max 200), `Body` (max 2000), `CreatedAt`, `ReadAt`; `static InAppNotification Create(Guid deliveryId, Guid userId, Guid notificationId, string typeCode, NotificationCategory category, string title, string body, DateTimeOffset now)`; `void MarkRead(DateTimeOffset now)` (idempotent, keeps the first time). `InAppNotificationErrors.NotFound(Guid id)` (`notifications.notification_not_found`).
- `internal sealed class UserPreference` (`UserId`, `TypeCode`, `Channel`, `IsEnabled`, `UpdatedAt`; key the three ids). `internal sealed class UserSettings : Entity<Guid>` (key `UserId`): `QuietHours? QuietHours`, `UpdatedAt`, `RowVersion`; `void SetQuietHours(QuietHours? quietHours, DateTimeOffset now)`.
- `internal sealed record QuietHours(TimeOnly Start, TimeOnly End)`: `static Result<QuietHours> Create(TimeOnly start, TimeOnly end)` (`start == end` gives `PreferenceErrors.InvalidQuietHours`, Validation, `notifications.invalid_quiet_hours`); `bool Contains(TimeOnly localTime)` (`[Start, End)`, crossing midnight when `End < Start`); `DateTimeOffset NextAllowedAt(DateTimeOffset now, TimeZoneInfo zone)`: `now` itself when outside the window, else the next local `End` converted to UTC; a local `End` that falls in a DST gap moves to the first valid instant after it, an ambiguous one takes the earlier offset.
- `PreferenceErrors`: `InvalidQuietHours`, `UnknownType(string typeCode)` (Validation, `notifications.unknown_type`, param `typeCode`), `ChannelNotSupported(string typeCode, string channel)` (Validation, `notifications.channel_not_supported`), `ChannelMandatory(string typeCode, string channel)` (Validation, `notifications.channel_mandatory`).
- `internal sealed class HubTicket` (key `TokenHash` `byte[32]`): `UserId`, `CreatedAt`, `ExpiresAt`, `ConsumedAt`; `static HubTicket Issue(Guid userId, byte[] tokenHash, TimeSpan lifetime, DateTimeOffset now)`; `bool CanConsume(DateTimeOffset now)` (unconsumed and `now < ExpiresAt`).

- [ ] **Step 1: Write the failing tests:** `Create_adds_one_delivery_per_channel_and_rejects_a_duplicate_channel`; `Retry_only_from_dead_lettered_and_not_after_expiry`; `DeliveryRetryPolicy` theory (`1→+1 min`, `2→+5 min`, `3→+15 min`, `4→+1 h` with max 5; `5→null` with max 5; `5→+6 h` with max 6; `9→+6 h` with max 10); `MarkRead_is_idempotent_and_keeps_the_first_time`; `QuietHours_create_rejects_equal_start_and_end`; `Contains_handles_a_window_across_midnight` (22:00-07:00: 23:30 and 06:59 in, 07:00 and 21:59 out); `NextAllowedAt_returns_now_outside_the_window`; `NextAllowedAt_in_Kuala_Lumpur_returns_seven_local_as_utc` (23:30 MYT on 2026-03-01 → 2026-03-01T23:00Z); `NextAllowedAt_across_spring_forward_in_New_York` (window 01:00-02:30, now 01:30 on 2026-03-08: 02:30 does not exist, result 03:00 EDT = 07:00Z); `NextAllowedAt_on_the_fall_back_day_uses_the_earlier_offset`; `HubTicket_cannot_be_consumed_at_or_after_expiry_or_twice`.
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement**; add the eight error codes to the three resx files (ms and zh-Hans are drafts) and the docs.
- [ ] **Step 4: Run** unit and architecture tests; expected PASS. **Commit** `feat(notifications): add notification, delivery and preference domain`.

### Task 7: Persistence

`Model: sonnet`

**Files:**
- Create: `Application/Abstractions/{INotificationRepository, IInAppNotificationRepository, IPreferenceRepository, IHubTicketRepository, IInbox}.cs`; `Infrastructure/Persistence/{NotificationsDbContext, NotificationConfiguration, DeliveryConfiguration, InAppNotificationConfiguration, UserPreferenceConfiguration, UserSettingsConfiguration, HubTicketConfiguration, NotificationRepository, InAppNotificationRepository, PreferenceRepository, HubTicketRepository, ModuleInbox}.cs`; `Infrastructure/Persistence/Migrations/*InitialNotifications*`
- Modify: `NotificationsModule.cs`, `docs/modules/notifications.md` (Data). The test factory needs no change: `MigrateModuleDatabasesAsync` picks up the new context and Respawn clears every schema.
- Test: `tests/TemplateName.IntegrationTests/Notifications/NotificationsPersistenceTests.cs`

**Interfaces:**
- `internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options), IUnitOfWork` with `internal const string Schema = "notify"`; `OnModelCreating` follows `AuthDbContext` (`ApplyDefaultConventions`, `HasDefaultSchema`, `ApplyConfigurationsFromAssembly`, `ApplyInbox()`; **no** `ApplyOutbox`, no aggregate here raises domain events).
- `IInbox` (module): `Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken)`, `void Record(Guid messageId, string consumer)`; `ModuleInbox` wraps `Inbox<NotificationsDbContext>`. `IUnitOfWork.SaveChangesAsync` plus `Task<bool> SaveChangesUnlessInboxDuplicateAsync(CancellationToken cancellationToken)` (false and the tracker cleared when the only failure is the inbox key).
- `INotificationRepository`: `void Add(Notification notification)`; `Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)` (tracked, with its notification). `IInAppNotificationRepository`: `Task<InAppNotification?> GetForUserAsync(Guid id, Guid userId, CancellationToken cancellationToken)`; `Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)` (one `ExecuteUpdateAsync` on unread rows created at or before `now`). `IPreferenceRepository`: `Task<IReadOnlyList<UserPreference>> ListAsync(Guid userId, CancellationToken cancellationToken)`, `Task<UserSettings?> GetSettingsAsync(Guid userId, CancellationToken cancellationToken)`, `void Add(UserPreference)`, `void Add(UserSettings)`. `IHubTicketRepository`: `void Add(HubTicket)`, `Task<Guid?> TryConsumeAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)` (one `UPDATE ... SET ConsumedAt = @now OUTPUT inserted.UserId WHERE TokenHash = @hash AND ConsumedAt IS NULL AND ExpiresAt > @now`).
- Schema (all `notify.`): `Notifications` (unique index `(SourceMessageId, TypeCode, RecipientUserId)`), `Deliveries` (FK to `Notifications`; worker index `(Status, NextAttemptAt) INCLUDE (Channel, LockedUntil)` filtered `Status = 0`; admin index `(CreatedAt DESC, Id DESC)`), `InAppNotifications` (index `(UserId, CreatedAt DESC, Id DESC)`; filtered index `(UserId)` `WHERE ReadAt IS NULL`), `UserPreferences` (PK `(UserId, TypeCode, Channel)`), `UserSettings` (PK `UserId`, `RowVersion`), `HubTickets` (PK `TokenHash varbinary(32)`), `InboxMessages`. `Data` and `ProtectedData` `nvarchar(max)`; `TypeCode` 100; `Culture` 16; `Destination` 320; `CorrelationId` 32.

- [ ] **Step 1: Write the failing integration tests:** `Notification_with_deliveries_round_trips`; `DateTime_values_round_trip_as_utc`; `Duplicate_source_message_type_and_recipient_violates_the_unique_index`; `Inbox_duplicate_save_returns_false_and_writes_nothing` (two contexts record the same message with a notification each; one true, one false, one notification row); `Hub_ticket_is_consumed_once_by_two_concurrent_callers` (`Task.WhenAll`; one user id, one null); `Expired_hub_ticket_is_not_consumed`; `Mark_all_read_leaves_rows_created_after_now`.
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement**; `AddNotificationsModule` registers `AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema)`, `AddInbox<NotificationsDbContext>()`, the repositories and `IUnitOfWork`.
- [ ] **Step 4: Create the migration** `InitialNotifications` with the `CLAUDE.md` command (`--project src/Modules/Notifications/TemplateName.Modules.Notifications --context NotificationsDbContext`); review the filtered indexes and key types in the generated SQL.
- [ ] **Step 5: Run** integration and architecture tests; expected PASS; fill the Data section. **Commit** `feat(notifications): add notifications persistence and migration`.

### Task 8: Template engine

`Model: opus (HTML encoding and secret rules)`

**Files:**
- Create: `Application/Abstractions/{INotificationRenderer, RenderedMessage, TemplateRenderException}.cs`, `Infrastructure/Templates/{EmbeddedTemplateStore, ScribanNotificationRenderer, TemplateOptions, TemplateVariables}.cs`, `Templates/Email/_layout.{en,ms,zh-Hans}.html.scriban`, `docs/adr/0019-embedded-scriban-notification-templates.md`
- Create (tests): `tests/TemplateName.UnitTests/Notifications/{ScribanNotificationRendererTests, TemplateCompletenessTests}.cs`, test-only templates under `tests/TemplateName.UnitTests/Notifications/Templates/...` with a `TestNotificationTypeSource`
- Modify: `Directory.Packages.props` (`Scriban`), the module csproj (`<EmbeddedResource Include="Templates\**\*.scriban" />`), `appsettings.json` (`Notifications:Templates`), `NotificationsModule.cs`, `docs/modules/notifications.md`, `docs/README.md`, overview ADR list

**Interfaces:**
- `internal sealed record RenderedMessage(string Subject, string TextBody, string? HtmlBody)`: email fills all three (`subject`, `text`, `html` inside the layout); in-app puts `title` in `Subject` and `body` in `TextBody`.
- `internal interface INotificationRenderer { RenderedMessage Render(string typeCode, NotificationChannel channel, string culture, IReadOnlyDictionary<string, string> variables); }`; throws `TemplateRenderException(string typeCode, NotificationChannel channel, string culture, string reason)` whose message never contains a variable value.
- `EmbeddedTemplateStore` (singleton): resource name `{assemblyName}.Templates.{Email|InApp}.{typeCode}.{culture}.{part}.scriban` in `NotificationCatalog.TemplateAssemblyOf(typeCode)`; looks up `culture`, then `en` (review 8.2: `zh-Hans` → `en`); parses once and caches the `Template`; the layout `Templates.Email._layout.{culture}.html.scriban` comes from the Notifications assembly.
- Renderer rules: Scriban `TemplateContext` with `StrictVariables = true`, `LoopLimit = 1000`, `RecursiveLimit = 20`, `CultureInfo.InvariantCulture`, `MemberRenamer` identity; a fresh `ScriptObject` per render holding the variables plus `product_name` from `TemplateOptions.ProductName` (section `Notifications:Templates`, default `TemplateName`); for `html` parts every value is `WebUtility.HtmlEncode`d before it enters the model, and the layout receives the rendered body as `content` unencoded; subject and title are single-line (newlines replaced by spaces) and at most 300 characters.

- [ ] **Step 1: Add `Scriban`** (latest stable) and run the licence job's command; confirm BSD-2-Clause and no usage fee in its licence file; record the licence in the ADR.
- [ ] **Step 2: Write the failing renderer tests** (test source and templates): `Renders_subject_text_and_html_inside_the_layout`; `Html_part_encodes_display_name` (`<script>` becomes `&lt;script&gt;` in HTML and stays literal in text); `Missing_variable_throws_render_exception_without_values` (Also pinned: a template with a missing placeholder); `Missing_culture_falls_back_to_en`; `Missing_en_template_throws`; `Rendering_ignores_current_ui_culture` (Review Focus 2: `CurrentUICulture = ms`, render for `zh-Hans`, Chinese text); `Numbers_and_dates_format_invariantly`; `Subject_is_single_line_and_capped`.
- [ ] **Step 3: Write the failing completeness tests** (`TemplateCompletenessTests`, over the real catalog and every registered source; theory data per type): `Every_type_has_every_part_for_every_default_channel_and_culture`; `Every_culture_uses_the_same_placeholders_as_en` (variables collected from the parsed Scriban AST); `Templates_use_only_declared_variables_and_product_name`; `Subjects_titles_and_in_app_bodies_never_use_secret_variables`; `No_orphan_template_resources` (every embedded `.scriban` maps to a catalog entry or a layout). With no Auth types yet, they run over the test source only.
- [ ] **Step 4: Run**; expected FAIL. **Step 5: Implement**; write the three layouts (product name in the footer; "You received this because you have an account" in en, ms, zh-Hans drafts); write ADR 0019 (D8; options: DB rows, Razor, Scriban files; consequences: template changes need a deploy, native review needed).
- [ ] **Step 6: Run** unit and architecture tests; expected PASS. **Commit** `feat(notifications): add scriban template rendering`.

### Task 9: Auth notification types and templates

`Model: sonnet`

**Files:**
- Create: `Application/Catalog/{AuthNotificationTypes, AuthNotificationTypeSource}.cs`, `Templates/Email/auth.*.{en,ms,zh-Hans}.{subject,text,html}.scriban`, `Templates/InApp/auth.{password_changed,token_reuse_detected}.{en,ms,zh-Hans}.{title,body}.scriban`
- Modify: `NotificationsModule.cs` (registers the source), `docs/modules/notifications.md` (types table)
- Test: `tests/TemplateName.UnitTests/Notifications/AuthNotificationTypesTests.cs` (the completeness tests of Task 8 now cover these)

**Interfaces:**
- `internal static class AuthNotificationTypes` constants and `AuthNotificationTypeSource : INotificationTypeSource`. Every type: category `Security`, priority `Critical`, email mandatory. Variables every type has: `display_name`. 

| Code | Source event | Channels (mandatory) | Variables | Secret |
|---|---|---|---|---|
| `auth.email_verification` | `EmailVerificationRequested` | Email (Email) | `display_name`, `expires_in_minutes` | `action_url` |
| `auth.password_reset` | `PasswordResetRequested` `SelfService` | Email (Email) | same | `action_url` |
| `auth.password_reset_required` | `PasswordResetRequested` `ForcedByAdmin` | Email (Email) | same | `action_url` |
| `auth.account_created` | `PasswordResetRequested` `CreatedByAdmin` | Email (Email) | same | `action_url` |
| `auth.registration_attempted` | `RegistrationAttempted` | Email (Email) | `display_name` | none |
| `auth.password_changed` | `PasswordChanged` | Email, InApp (Email) | `display_name`, `occurred_at`, `time_zone` | none |
| `auth.account_locked` | `UserLockedOut` | Email (Email) | `display_name`, `locked_until`, `time_zone` | none |
| `auth.token_reuse_detected` | `RefreshTokenReuseDetected` | Email, InApp (Email) | `display_name`, `occurred_at`, `time_zone` | none |

- Copy (drafts; ms and zh-Hans need native review, Part C1). Subjects, and in-app titles where the type has in-app:

| Code | en | ms | zh-Hans |
|---|---|---|---|
| `auth.email_verification` | Confirm your email address | Sahkan alamat e-mel anda | 请确认您的电子邮件地址 |
| `auth.password_reset` | Reset your password | Tetapkan semula kata laluan anda | 重置您的密码 |
| `auth.password_reset_required` | An administrator asked you to set a new password | Pentadbir meminta anda menetapkan kata laluan baharu | 管理员要求您设置新密码 |
| `auth.account_created` | Your account is ready: set your password | Akaun anda sedia: tetapkan kata laluan anda | 您的账户已创建：请设置密码 |
| `auth.registration_attempted` | Sign-up attempt with your email address | Cubaan pendaftaran dengan alamat e-mel anda | 有人尝试用您的电子邮件地址注册 |
| `auth.password_changed` | Your password was changed | Kata laluan anda telah ditukar | 您的密码已更改 |
| `auth.account_locked` | Your account is temporarily locked | Akaun anda dikunci buat sementara waktu | 您的账户已被暂时锁定 |
| `auth.token_reuse_detected` | We signed out a session to protect your account | Kami telah menamatkan satu sesi untuk melindungi akaun anda | 为保护您的账户，我们已注销一个会话 |

- Body content (each culture says the same; the English wording follows Plan 2's `AuthEmails`): the link types show a button and the copyable address, say the link works once and expires in `{{ expires_in_minutes }}` minutes, and say what to do if the reader did not ask (ignore it; for `password_reset_required` and `account_created`, contact the administrator). `registration_attempted` repeats Plan 2's notice without a link. `password_changed` and `token_reuse_detected` give the time (`{{ occurred_at }} ({{ time_zone }})`) and say: if this was not you, reset your password now. `account_locked` gives `{{ locked_until }} ({{ time_zone }})` and says the lock lifts by itself; if it was not you, reset your password. In-app bodies are one or two sentences without any link.

- [ ] **Step 1: Write the failing tests:** `Every_auth_type_is_security_critical_with_mandatory_email`; `Only_password_changed_and_token_reuse_have_an_optional_in_app_channel`; the Task 8 completeness tests now fail for the missing files.
- [ ] **Step 2: Write the 84 template files** (72 email parts, 12 in-app parts; the 3 layouts exist from Task 8) with the copy above. **Step 3: Run** unit and architecture tests; expected PASS.
- [ ] **Step 4: Document** the types table and the copy-review status in `docs/modules/notifications.md`. **Commit** `feat(notifications): add auth security notification templates`.

### Task 10: Scheduler and Auth event consumers

`Model: opus (idempotency, secrets, quiet hours)`

**Files:**
- Create: `Application/Scheduling/{NotificationRequest, NotificationScheduler, ScheduleOutcome}.cs`, `Application/AuthEvents/{EmailVerificationRequestedIntegrationEventHandler, PasswordResetRequestedIntegrationEventHandler, RegistrationAttemptedIntegrationEventHandler, PasswordChangedIntegrationEventHandler, UserLockedOutIntegrationEventHandler, RefreshTokenReuseDetectedIntegrationEventHandler}.cs`, `Infrastructure/Observability/{NotificationsMetrics, INotificationsMetrics}.cs` (meter `TemplateName.Notifications`)
- Modify: `NotificationsModule.cs` (meter added with `ConfigureOpenTelemetryMeterProvider`), `docs/modules/notifications.md` (Events, Background processing)
- Test: `tests/TemplateName.UnitTests/Notifications/{NotificationSchedulerTests, AuthEventHandlerTests}.cs`, `tests/TemplateName.IntegrationTests/Notifications/AuthEventConsumptionTests.cs`

**Interfaces:**
- Consumes: `IIntegrationEventHandler<T>`, Task 3 events and `IUserContactDirectory`, `ISecretProtector`, `NotificationCatalog`, `RecipientCulture`, `TimeZoneResolver`, `QuietHours`, `IInbox`, `IUnitOfWork`, repositories.
- `internal sealed record NotificationRequest(string TypeCode, Guid RecipientUserId, Guid SourceMessageId, string Consumer, IReadOnlyDictionary<string, string> Variables, IReadOnlyDictionary<string, string> ProtectedVariables, string? EmailOverride, DateTimeOffset? ExpiresAt)`; `ProtectedVariables` values are already ciphertext (from the event) and are stored as is.
- `internal enum ScheduleOutcome { Scheduled, AlreadyProcessed, RecipientNotFound }`; `internal sealed class NotificationScheduler` with `Task<ScheduleOutcome> ScheduleAsync(NotificationRequest request, Func<UserContact, IReadOnlyDictionary<string, string>>? contactVariables, CancellationToken cancellationToken)`. Order: inbox `HasProcessedAsync` → `AlreadyProcessed`; directory lookup (null: record the inbox row, save, `RecipientNotFound`, Information log with the user id); culture = `RecipientCulture.Resolve(contact.Locale)`; variables = request variables + `display_name` + `contactVariables(contact)` (used for local times); channels = type defaults minus channels the user disabled (mandatory ones always kept); for each channel `firstAttemptAt = now`, except an **email** of a non-`Critical` type inside the user's quiet hours: `QuietHours.NextAllowedAt(now, zone)`; email destination = `EmailOverride ?? contact.Email`; record the inbox row; `SaveChangesUnlessInboxDuplicateAsync` (false → `AlreadyProcessed`). Throws only for infrastructure failures, so the Auth outbox retries. Unknown type code throws `InvalidOperationException` (a programming error).
- Handlers (`internal sealed`, consumer name = handler `FullName`): map each event per the Task 9 table; link events pass `ProtectedVariables["action_url"] = ProtectedActionUrl`, `EmailOverride = Email`, `ExpiresAt`, and `expires_in_minutes = ceil((ExpiresAt - now) / 1 min)`; time variables are formatted `yyyy-MM-dd HH:mm` (invariant) in the recipient's zone, `time_zone` is the IANA id or `UTC`.
- Metrics: counter `notifications.created` (tag `type`, the type code); the worker adds more in Task 11. Tags never carry a user id or address.

- [ ] **Step 1: Write the failing unit tests** (NSubstitute): `Disabled_optional_channel_is_skipped_but_mandatory_email_kept`; `Normal_email_inside_quiet_hours_is_deferred_to_the_window_end` (test type); `Security_notice_ignores_quiet_hours` (Review Focus 5); `In_app_is_never_deferred`; `Invalid_time_zone_is_treated_as_utc` (Review Focus 5, warning logged with the user id only); `Unsupported_locale_is_snapshotted_as_en` (Review Focus 2); `Missing_recipient_records_the_inbox_and_creates_nothing`; `Protected_values_are_stored_unchanged_and_never_decrypted`; `Already_processed_message_does_nothing`; per handler: `Maps_*_to_its_type_and_variables` (reset reasons to the three codes).
- [ ] **Step 2: Write the failing integration tests** (`AuthEventConsumptionTests`, real host, events published through `IIntegrationEventPublisher` with an explicit id): `Password_changed_creates_email_and_in_app_deliveries_in_the_user_culture`; `Same_event_consumed_twice_creates_one_notification` (Review Focus 1); `Concurrent_duplicate_consumption_creates_one_notification` (`Task.WhenAll` of two publishes of one event; one notification row, no exception); `Stored_rows_never_contain_the_plaintext_link` (scan `notify.Notifications` and `notify.Deliveries` for the link); `Unknown_user_creates_nothing_and_does_not_throw`.
- [ ] **Step 3: Run**; expected FAIL. **Step 4: Implement** and document (Events: the consumed integration events and their types; Background processing: the inbox rule).
- [ ] **Step 5: Run** all suites; expected PASS. **Commit** `feat(notifications): consume auth events through the inbox`.

### Task 11: Delivery worker and in-app channel

`Model: opus (concurrency and lease)`

**Files:**
- Create: `Application/Abstractions/{INotificationChannel, ChannelSendResult, DeliveryWork, IInAppNotificationPusher}.cs`, `Infrastructure/Delivery/{DeliveryOptions, DeliveryDispatcher, DeliveryBackgroundService}.cs`, `Infrastructure/Channels/InApp/{InAppChannel, NullInAppNotificationPusher}.cs`
- Modify: `NotificationsModule.cs`, `NotificationsMetrics.cs`, `appsettings.json` (`Notifications:Delivery`), `IntegrationTestWebAppFactory.cs` (`Notifications:Delivery:Enabled=false`; a `FakeEmailChannel` until Task 12), `IntegrationTestBase.cs` (`DispatchNotificationsAsync`), `docs/modules/notifications.md`, `docs/services/api.md` (configuration)
- Test: `tests/TemplateName.UnitTests/Notifications/DeliveryOptionsTests.cs`, `tests/TemplateName.IntegrationTests/Notifications/{DeliveryDispatcherTests, InAppChannelTests}.cs`, `tests/TemplateName.IntegrationTests/Infrastructure/FakeEmailChannel.cs`

**Interfaces:**
- `internal enum ChannelSendOutcome { Sent, TransientFailure, PermanentFailure }`; `internal sealed record ChannelSendResult(ChannelSendOutcome Outcome, string? Reason)` with `static Sent`, `Transient(string reason)`, `Permanent(string reason)`; a reason is a short code (`smtp_451`, `smtp_550`, `invalid_address`, `timeout`, `render_failed`), never free text.
- `internal sealed record DeliveryWork(Guid DeliveryId, Guid NotificationId, Guid RecipientUserId, string TypeCode, NotificationCategory Category, string? Destination, RenderedMessage Message)`; `internal interface INotificationChannel { NotificationChannel Channel { get; } Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken); }`.
- `internal interface IInAppNotificationPusher { Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken); }` with `internal sealed record InAppNotificationPushed(Guid Id, string TypeCode, NotificationCategory Category, string Title, string Body, DateTime CreatedAt)`; `NullInAppNotificationPusher` until Task 15.
- `InAppChannel`: inserts `InAppNotification.Create(work.DeliveryId, ...)` and returns `Sent`; the dispatcher saves it together with the `Sent` update; a duplicate key (a retry after a crash) counts as `Sent`. After the save, `PushAsync` is called best effort (an exception is logged at Warning and ignored).
- `DeliveryOptions` (section `Notifications:Delivery`): `Enabled` true, `PollingInterval` 00:00:05 (1 s - 1 h), `BatchSize` 10 (1-100), `MaxAttempts` 5 (1-10), `LeaseDuration` 00:05:00 (30 s - 1 h).
- `public sealed class DeliveryDispatcher { public Task<int> ProcessBatchAsync(CancellationToken cancellationToken); }` (singleton; internal types behind it). Claim: the outbox's SQL shape over `[notify].[Deliveries]` (`WITH (UPDLOCK, READPAST, ROWLOCK)`, `Status = 0 AND NextAttemptAt <= @Now AND (LockedUntil IS NULL OR LockedUntil < @Now)`, `ORDER BY NextAttemptAt`, `OUTPUT` id, attempt count and lease). Per delivery, in its own scope, stopping once the lease has run out: load the delivery and notification; **expired** (`ExpiresAt <= now`) → `Expired`; else decrypt `ProtectedData` into the variables (email only; a `CryptographicException` is a permanent `render_failed`), render with the notification's snapshotted `Culture` (a `TemplateRenderException` is permanent `render_failed`), call the channel registered for the delivery's channel (none registered: permanent `channel_unavailable`); then one update **guarded by the claimed lease**: `Sent` (`SentAt`, `RenderedSubject`), or a failure (`AttemptCount + 1`, `LastError = reason`, `NextAttemptAt = DeliveryRetryPolicy.NextAttemptAt(...)`, and `DeadLettered` with `DeadLetteredAt` when that is null or the failure is permanent). The decrypted values and the rendered bodies are never stored or logged.
- `DeliveryBackgroundService` runs the dispatcher like `OutboxBackgroundService` (drains full batches, then waits `PollingInterval` on `TimeProvider`; a poll failure is logged and the loop continues; it stops on cancellation, leaving claimed rows to their lease).
- Metrics (`TemplateName.Notifications`): `notifications.deliveries` (tags `channel`, `result` = `sent`, `retry`, `dead_lettered`, `expired`), histogram `notifications.delivery.duration` (ms, tag `channel`).
- `IntegrationTestBase.DispatchNotificationsAsync()`: loops the Auth outbox dispatcher and then the delivery dispatcher until both return 0, without moving the clock.

- [ ] **Step 1: Write the failing integration tests** (`DeliveryDispatcherTests`, deliveries created through the scheduler with the test type; `FakeEmailChannel` counts sends and can fail transiently or permanently on demand): `Due_delivery_is_sent_once_and_marked_sent`; `Deferred_delivery_waits_until_next_attempt_at` (`Factory.Time`); `Transient_failures_follow_the_schedule_and_dead_letter_after_five` (Review Focus 3: attempts at +0, +1 min, +5 min, +15 min, +1 h; `DeadLettered` after the fifth; no sixth send after +6 h); `Permanent_failure_dead_letters_at_once`; `Expired_link_is_not_sent` (Also pinned: a reset delivery failing transiently until past 30 minutes becomes `Expired`); `Concurrent_dispatchers_send_each_delivery_once` (Review Focus 4: 40 deliveries, two dispatchers looping concurrently until both return 0; 40 sends, 40 distinct ids); `Lease_lost_update_does_not_overwrite_the_new_owner` (the gate pattern of `OutboxTests`); `Missing_template_dead_letters_with_render_failed`; `Plaintext_link_reaches_the_channel_but_no_row_or_log` (recording logger provider). `InAppChannelTests`: `In_app_delivery_creates_one_row_with_the_delivery_id`; `Retry_after_crash_does_not_duplicate_the_row`; `Push_failure_does_not_fail_the_delivery`.
- [ ] **Step 2: Write the failing unit test** `DeliveryOptionsTests` (ranges). **Step 3: Run**; expected FAIL.
- [ ] **Step 4: Implement** and document (Background processing: the worker, the retry table, at-least-once email, exactly-once in-app; Configuration; Observability).
- [ ] **Step 5: Run** all suites; expected PASS. **Commit** `feat(notifications): add delivery worker with retries and dead-letter`.

### Task 12: Email channel and cut-over from Auth

`Model: opus (removes the working email path)`

**Files:**
- Create: `Application/Abstractions/{IEmailTransport, EmailMessage, EmailSendException}.cs`, `Infrastructure/Channels/Email/{EmailOptions, SmtpEmailTransport, EmailChannel, LegacyEmailSettingsCheck}.cs`
- Delete (Auth): `Application/Abstractions/IEmailSender.cs`, `Application/Verification/{AuthEmails, SendVerificationEmailDomainEventHandler, SendRegistrationAttemptedEmailDomainEventHandler}.cs`, `Infrastructure/Email/{EmailOptions, SmtpEmailSender}.cs`, the MailKit reference of the Auth csproj; tests `UnitTests/Auth/{AuthEmailsTests, EmailOptionsTests, SendVerificationEmailDomainEventHandlerTests, SendRegistrationAttemptedEmailDomainEventHandlerTests}.cs`
- Move: `tests/TemplateName.IntegrationTests/Auth/{SmtpEmailSenderTests, MailpitFixture, RecordingEmailSenderTests}.cs` → `tests/TemplateName.IntegrationTests/Notifications/` (renamed `SmtpEmailTransportTests`); `RecordingEmailSender` implements `IEmailTransport` and gains `FailNext(EmailSendException)` and `FailAlways`
- Modify: `AuthModule.cs`, `NotificationsModule.cs`, `appsettings.json` and `appsettings.Development.json` (`Auth:Email` → `Notifications:Email`), `.env.example` if it names the keys, `IntegrationTestWebAppFactory.cs` (registers `RecordingEmailSender` as `IEmailTransport`; removes `FakeEmailChannel`), every Auth integration test that dispatched the Auth outbox to read emails (now `DispatchNotificationsAsync()`), `docs/modules/{auth,notifications}.md`, `docs/services/api.md`
- Test: `tests/TemplateName.UnitTests/Notifications/{EmailOptionsTests, LegacyEmailSettingsCheckTests, EmailChannelTests}.cs`

**Interfaces:**
- `internal sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody)` (moved); `internal interface IEmailTransport { Task SendAsync(EmailMessage message, CancellationToken cancellationToken); }`; `internal sealed class EmailSendException(bool isPermanent, string reason) : Exception` (message is the reason only).
- `SmtpEmailTransport`: Plan 2's `SmtpEmailSender` with classification: `MailboxAddress.Parse` failure → permanent `invalid_address`; `SmtpCommandException` with `ErrorCode` `RecipientNotAccepted` or `SenderNotAccepted` and a 5xx status → permanent `smtp_{code}`; any other SMTP or socket error → transient `smtp_{code}` or the exception type name in snake case. Logs as before (never the address).
- `EmailChannel : INotificationChannel` (`Email`): builds the `EmailMessage` from the work and maps `EmailSendException` to the result.
- `EmailOptions` (section **`Notifications:Email`**): the same keys, defaults and ranges as Plan 2's `Auth:Email` (`Host` `localhost`, `Port` 1025, `UseTls` false, `Username`/`Password` empty, `From` `no-reply@localhost.test`, `FromName` `TemplateName`, `Timeout` 00:00:30).
- `LegacyEmailSettingsCheck`: an options validation that fails start-up when any key under `Auth:Email` exists in configuration, with the message `Auth:Email has moved to Notifications:Email (see docs/modules/notifications.md#configuration).` (D15).

- [ ] **Step 1: Write the failing tests:** `SmtpEmailTransportTests` against Mailpit (the moved tests plus `Rejected_recipient_is_classified_permanent` using Mailpit's SMTP rejection setting, or a stub `SmtpClient` seam if Mailpit cannot reject); `EmailChannelTests` (`Maps_permanent_and_transient_failures`); `LegacyEmailSettingsCheckTests` (`Old_section_stops_the_host_with_a_pointer`); integration `Rejected_recipient_is_dead_lettered_without_retry_and_logs_no_address` (Review Focus 3: `RecordingEmailSender.FailNext(permanent smtp_550)`, one attempt, `DeadLettered`, `LastError = smtp_550`, the address absent from every captured log line and from `LastError`).
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement** the channel and delete the Auth email path. Every Auth integration test that read `Factory.EmailSender` now calls `DispatchNotificationsAsync()`; `LastLinkToken(to)` keeps working because the rendered link still carries `token=`.
- [ ] **Step 4: Document:** Auth doc (Events now publish integration events, the email sections point to Notifications, the "English only" known limit is gone); Notifications doc (Configuration with the D15 migration note); `docs/services/api.md` configuration sections.
- [ ] **Step 5: Run** the whole suite; expected PASS (every Plan 2 email test now goes through Notifications). The commit body says `BREAKING CHANGE: Auth:Email settings moved to Notifications:Email.` **Commit** `feat(notifications)!: deliver auth emails through notifications`.

### Task 13: In-app inbox API

`Model: sonnet`

**Files:**
- Create: `Application/Inbox/{List,UnreadCount,MarkRead,MarkAllRead}/...` (query or command, handler, validator, response per use case), `Application/Inbox/List/InAppNotificationSortFields.cs`, `Endpoints/{NotificationEndpoints.cs, ListInboxRequest.cs}`
- Modify: `NotificationsModule.cs` (`MapNotificationsEndpoints` maps the `notifications` group), resx, `docs/modules/notifications.md`
- Test: `tests/TemplateName.UnitTests/Notifications/Inbox/*`, `tests/TemplateName.IntegrationTests/Notifications/InboxEndpointTests.cs`

**Interfaces:** every route `.RequireAuthorization()` explicitly (Plan 2 ruling R13); the user is `ICurrentUser.UserId`.
- `ListInboxQuery(CursorPageRequest Page, bool UnreadOnly) : IQuery<CursorPage<InAppNotificationResponse>>`; `sealed record InAppNotificationResponse(Guid Id, string TypeCode, NotificationCategory Category, string Title, string Body, DateTime CreatedAt, DateTime? ReadAt)`; `GET /api/v1/notifications/inbox?unreadOnly=` (Dapper, owner filter in SQL; sort allow-list `createdAt` only, default `-createdAt`, `Id` tie-breaker; the cursor is bound to `unreadOnly`).
- `GetUnreadCountQuery() : IQuery<UnreadCountResponse>`; `sealed record UnreadCountResponse(int Count, bool IsCapped)`; `GET /api/v1/notifications/inbox/unread-count` counts at most 1000 rows (`SELECT COUNT(*) FROM (SELECT TOP (1000) ...)`), `IsCapped` when it hit 1000.
- `MarkReadCommand(Guid NotificationId) : ICommand` → `POST /api/v1/notifications/inbox/{id:guid}/read` 204 (idempotent); another user's or an unknown id gives 404 `notifications.notification_not_found`.
- `MarkAllReadCommand() : ICommand` → `POST /api/v1/notifications/inbox/read-all` 204: marks unread rows created at or before the server's `now`.

- [ ] **Step 1: Write the failing tests:** unit (`MarkRead_of_another_users_row_is_not_found`); integration: `List_shows_only_my_notifications_newest_first`; `Unread_only_filter_and_cursor_mismatch` (a cursor from the unfiltered list is 400 `pagination.cursor_mismatch`); `Paging_with_equal_timestamps_visits_every_row_once` (Also pinned: 45 rows on 3 instants, forward and backward); `Rows_inserted_between_pages_cause_no_duplicates`; `Unread_count_is_capped_at_1000` (1005 rows inserted in bulk; `Count = 1000`, `IsCapped = true`); `Mark_read_is_idempotent_and_404_for_others`; `Read_all_marks_only_my_rows`; `Every_route_requires_a_token`.
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement** and document the endpoints (the documentation test checks every route). **Step 4: Run** all suites; expected PASS.
- [ ] **Step 5: Commit** `feat(notifications): add in-app inbox endpoints`.

### Task 14: Preferences and quiet hours API

`Model: sonnet`

**Files:**
- Create: `Application/Preferences/{Get,Update,GetQuietHours,UpdateQuietHours}/...`, `Endpoints/{PreferenceRequests.cs}` (routes added to `NotificationEndpoints.cs`)
- Modify: resx, `docs/modules/notifications.md`
- Test: `tests/TemplateName.UnitTests/Notifications/Preferences/*`, `tests/TemplateName.IntegrationTests/Notifications/PreferenceEndpointTests.cs`

**Interfaces:** all `.RequireAuthorization()`.
- `GetPreferencesQuery() : IQuery<PreferencesResponse>`; `sealed record PreferencesResponse(IReadOnlyList<TypePreferenceResponse> Types)`; `sealed record TypePreferenceResponse(string TypeCode, NotificationCategory Category, IReadOnlyList<ChannelPreferenceResponse> Channels)`; `sealed record ChannelPreferenceResponse(NotificationChannel Channel, bool Enabled, bool Mandatory)`; `GET /api/v1/notifications/preferences` lists every catalog type in code order (a fixed, code-defined set, not a growing list, so it is not paged; say so in the doc).
- `UpdatePreferencesCommand(IReadOnlyList<ChannelPreferenceUpdate> Items) : ICommand<PreferencesResponse>`; `sealed record ChannelPreferenceUpdate(string TypeCode, NotificationChannel Channel, bool Enabled)`; `PUT /api/v1/notifications/preferences` upserts the given items only (1-100 items); unknown type 400 `notifications.unknown_type`, a channel the type does not have 400 `notifications.channel_not_supported`, disabling a mandatory channel 400 `notifications.channel_mandatory` (enabling it is a no-op). The whole request is rejected if any item is invalid.
- `GetQuietHoursQuery() : IQuery<QuietHoursResponse>`; `sealed record QuietHoursResponse(TimeOnly? Start, TimeOnly? End, string TimeZone)` (the time zone comes from the directory, read-only here; change it with `PUT /api/v1/auth/me`); `UpdateQuietHoursCommand(TimeOnly? Start, TimeOnly? End) : ICommand<QuietHoursResponse>` → `PUT /api/v1/notifications/preferences/quiet-hours`: both null clears; exactly one null, or equal values, is 400 `notifications.invalid_quiet_hours`. JSON times are `"HH:mm"`.

- [ ] **Step 1: Write the failing tests:** unit (validators: 101 items, empty list, one-sided quiet hours); integration: `Defaults_show_every_type_with_mandatory_flags`; `Disabling_in_app_for_password_changed_is_stored_and_honoured` (publish `PasswordChanged`; only the email delivery is created); `Disabling_mandatory_email_is_rejected_and_nothing_changes`; `Unknown_type_and_unsupported_channel_are_rejected`; `Quiet_hours_round_trip_and_clear`; `Quiet_hours_response_shows_the_auth_time_zone`; `Every_route_requires_a_token`.
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement** and document. **Step 4: Run** all suites; expected PASS.
- [ ] **Step 5: Commit** `feat(notifications): add preferences and quiet hours endpoints`.

### Task 15: SignalR hub with tickets

`Model: opus (authentication)`

**Files:**
- Create: `Application/HubTickets/Issue/{IssueHubTicketCommand, IssueHubTicketCommandHandler, HubTicketResponse}.cs`, `Infrastructure/Hub/{NotificationsHub, HubOptions, HubTicketAuthenticationHandler, HubTicketAuthenticationOptions, SubClaimUserIdProvider, SignalRInAppNotificationPusher}.cs`, `docs/adr/0021-signalr-hub-tickets.md`
- Modify: `NotificationsModule.cs` (`AddSignalR`, the `HubTicket` scheme, `MapNotificationsHub`), `NotificationEndpoints.cs`, `Directory.Packages.props` (`Microsoft.AspNetCore.SignalR.Client`, test project only), `appsettings.json` (`Notifications:Hub`), `docs/modules/notifications.md` (with a JS client example), `docs/services/api.md` (hub mapping, CORS note), `docs/README.md`, overview ADR list
- Test: `tests/TemplateName.UnitTests/Notifications/HubTicketAuthenticationHandlerTests.cs`, `tests/TemplateName.IntegrationTests/Notifications/HubTests.cs`

**Interfaces:**
- `IssueHubTicketCommand() : ICommand<HubTicketResponse>`; `sealed record HubTicketResponse(string Ticket, DateTimeOffset ExpiresAt)`; `POST /api/v1/notifications/hub-tickets` (`.RequireAuthorization()`, `Cache-Control: no-store`): 32 random bytes base64url from `RandomNumberGenerator`, stored as `SHA256` hash with `HubTicket.Issue`.
- `HubOptions` (section `Notifications:Hub`): `TicketLifetime` 00:00:30 (5 s - 5 min), `ConnectionLifetime` 00:10:00 (1 min - 1 h).
- `HubTicketAuthenticationHandler` (scheme `"HubTicket"`): handles only requests whose path starts with `/hubs/notifications`; reads the `access_token` query value, else the `Authorization: Bearer` value (negotiate); `IHubTicketRepository.TryConsumeAsync(hash, now)`; success gives a principal with `sub` and `AuthenticationProperties.ExpiresUtc = now + ConnectionLifetime`; otherwise `NoResult` (absent) or `Fail` (unknown, expired, used). The JWT bearer handler is **not** configured to read query strings (assert in a test).
- Hub: `internal sealed class NotificationsHub : Hub` with no client-callable methods; `MapHub<NotificationsHub>("/hubs/notifications", options => { options.Transports = HttpTransportType.WebSockets; options.CloseOnAuthenticationExpiration = true; }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "HubTicket").RequireAuthenticatedUser())`; `SubClaimUserIdProvider : IUserIdProvider` returns `sub`. Server event `notificationReceived` with `InAppNotificationPushed`.
- `SignalRInAppNotificationPusher : IInAppNotificationPusher` replaces the null pusher: `Clients.User(userId.ToString()).SendAsync("notificationReceived", payload)`.
- Logs: the request-logging path already omits the query string; this task adds a test that neither the Serilog request event nor the ASP.NET Core activity's `url.query` tag carries the ticket value (if the OpenTelemetry instrumentation version in use does not redact, add an `EnrichWithHttpRequest` that sets `url.query` to the redacted form).

- [ ] **Step 0: Add `Microsoft.AspNetCore.SignalR.Client`** to `Directory.Packages.props` and reference it from the integration test project only; run the licence job's command (MIT expected).
- [ ] **Step 1: Write the failing tests:** unit (`Ticket_outside_the_hub_path_is_ignored`, `Ticket_in_the_bearer_header_is_accepted_on_negotiate`, `Absent_ticket_is_no_result`, `Used_or_expired_ticket_fails`, `Principal_expires_after_connection_lifetime`); integration (`HubTests`, `HubConnection` from `Microsoft.AspNetCore.SignalR.Client`, WebSockets transport, `HttpMessageHandlerFactory` = the test server's handler and `WebSocketFactory` over `TestServer.CreateWebSocketClient()`, `AccessTokenProvider` fetching a new ticket on each call): `Connected_client_receives_its_in_app_notification` (publish `PasswordChanged`, dispatch, the client gets `notificationReceived` with the same id as the inbox row); `Another_users_notification_is_not_received`; `Expired_ticket_cannot_connect` (Also pinned: `Factory.Time` + 31 s → 401 on negotiate); `Reused_ticket_cannot_connect`; `Jwt_in_query_string_is_rejected` (`?access_token=<valid JWT>` → 401); `Ticket_endpoint_requires_a_token_and_is_no_store`; `Ticket_value_is_not_logged_or_traced` (recording log sink and `ActivityListener`).
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement**; write ADR 0021 (review 2.10; options: JWT in query with redaction, cookie, ticket; decision D12; consequences: one extra round trip per connect, ticket table cleanup in Plan 5, D13 scale-out).
- [ ] **Step 4: Run** all suites; expected PASS. **Commit** `feat(notifications): add signalr hub with single-use tickets`.

### Task 16: Admin deliveries and permissions

`Model: sonnet`

**Files:**
- Create: `Application/NotificationsPermissions.cs`, `Application/NotificationsPermissionSource.cs`, `Application/Admin/Deliveries/{List,Retry}/...`, `Application/Admin/Deliveries/List/DeliverySortFields.cs`, `Application/Admin/DestinationMask.cs`, `Endpoints/{AdminDeliveryEndpoints.cs, AdminDeliveryRequests.cs}`
- Modify: `src/BuildingBlocks/TemplateName.Application.Common/Identity/PermissionDefinition.cs` (+`bool AdminDefault = false`), Auth `AuthPermissionSource.cs` (its eight Admin defaults use the flag), Auth `Infrastructure/Authorization/{AuthSeeder, PermissionSynchronizer}.cs` (grant newly inserted flagged codes to `Admin`), `NotificationsModule.cs`, resx, `docs/modules/{notifications,auth}.md`, `docs/building-blocks/application-common.md`
- Test: `tests/TemplateName.UnitTests/Notifications/{NotificationsPermissionSourceTests, DestinationMaskTests}.cs`, `tests/TemplateName.IntegrationTests/Notifications/AdminDeliveryTests.cs`, `tests/TemplateName.IntegrationTests/Auth/AuthSeederTests.cs` (extend)

**Interfaces:**
- `NotificationsPermissions`: `notifications.delivery.view`, `notifications.delivery.retry`, both `AdminDefault = true`; module `notifications`.
- `ListDeliveriesQuery(CursorPageRequest Page, DeliveryStatus? Status, NotificationChannel? Channel, string? TypeCode, Guid? RecipientUserId) : IQuery<CursorPage<DeliveryResponse>>`; `sealed record DeliveryResponse(Guid Id, Guid NotificationId, string TypeCode, Guid RecipientUserId, NotificationChannel Channel, string? Destination, DeliveryStatus Status, int AttemptCount, DateTime? NextAttemptAt, string? LastError, DateTime CreatedAt, DateTime? SentAt, DateTime? DeadLetteredAt)`; `GET /api/v1/admin/notifications/deliveries` (`.RequirePermission(notifications.delivery.view)`; Dapper; sort `createdAt` default `-createdAt`; `Destination` masked by `DestinationMask.Mask` (`alice@example.com` → `a****@example.com`)).
- `RetryDeliveryCommand(Guid DeliveryId) : ICommand` → `POST /api/v1/admin/notifications/deliveries/{id:guid}/retry` (`.RequirePermission(notifications.delivery.retry)`) 202; 404 `notifications.delivery_not_found`, 409 `notifications.delivery_not_retryable`, 409 `notifications.delivery_expired`.
- Seeder rule: in the run that **inserts** a code whose definition has `AdminDefault`, `Admin` receives it; a later run never re-adds it (an administrator may have removed it).

- [ ] **Step 1: Write the failing tests:** unit (`Codes_are_unique_module_resource_action`, `Mask_*`); seeder (`Admin_receives_flagged_permission_when_first_declared_but_not_after_removal`, `Auth_admin_defaults_are_unchanged`); integration (`AdminDeliveryTests`): `Anonymous_is_401_and_without_permission_is_403` for both routes; `List_filters_by_status_channel_type_and_recipient_and_masks_destination`; `List_pages_by_cursor`; `Retry_resends_a_dead_letter_once` (dead-letter through `RecordingEmailSender.FailAlways`, clear, retry, dispatch: one email, `Sent`); `Retry_of_a_sent_delivery_is_409`; `Retry_of_an_expired_link_is_409`.
- [ ] **Step 2: Run**; expected FAIL. **Step 3: Implement** and document (permissions table in both module docs, `AdminDefault` in Application.Common). **Step 4: Run** all suites; expected PASS.
- [ ] **Step 5: Commit** `feat(notifications): add admin delivery list and retry`.

### Task 17: End-to-end flow, docs, snapshot, smoke

`Model: sonnet`

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Notifications/NotificationFlowTests.cs`
- Modify: `docs/modules/notifications.md` (complete every section: endpoints with success and error codes and the hub, domain model with the delivery state diagram, error codes with params and English messages, events consumed, every `Notifications:*` key, data tables and the migration, background processing with the retry table, observability with the meter, testing, known limits: D13 live pushes per instance, at-least-once email, time-zone change after deferral, `ms`/`zh-Hans` drafts, retention arrives with Plan 5), `docs/modules/auth.md` (Contracts, events, the removed email sections, D15), `docs/architecture/overview.md` (module map with the Auth → Notifications event arrow, data ownership of `notify`, ADRs 0018-0021), `docs/services/api.md` (registration order, `MapNotificationsHub`, configuration sections `Notifications:Email`, `Notifications:Delivery`, `Notifications:Templates`, `Notifications:Hub`), `docs/building-blocks/*.md`, `docs/README.md`, `README.md` and `build/template-content/README.md` (quick start: Mailpit shows the rendered emails; the notification inbox and hub), the OpenAPI snapshot
- Modify: `build/scripts/template-smoke.sh` only if the generated project needs a new exclusion

**Interfaces:** none new.

- [ ] **Step 1: Write the end-to-end test** (`NotificationFlowTests.Register_confirm_login_change_password_notifies_by_email_and_in_app`): register with locale `zh-Hans` → `DispatchNotificationsAsync` → the confirmation email is Chinese and its link confirms the account → login → connect the hub with a ticket → change password → dispatch → the email (Chinese subject `您的密码已更改`) and the in-app row exist, `GET /inbox` shows it, `unread-count` is 1, the hub received it → mark read → count 0. Real host, database, outbox, inbox and worker; only the SMTP transport is `RecordingEmailSender`.
- [ ] **Step 2: Add** `Auth_outbox_retry_sends_one_email` (Review Focus 1 end to end: a failing extra Auth domain handler makes the Auth outbox retry the message after Notifications consumed it; after two dispatch rounds exactly one email).
- [ ] **Step 3: Run** the whole suite with coverage (`dotnet test -c Release --coverage`, the repo's coverlet configuration). Expected: green; Notifications `Domain` and `Application` line coverage at least 80 % (the wildcard in `coverage.testconfig.json` already includes the module; add tests rather than lowering the gate).
- [ ] **Step 4: Documentation and snapshot:** run the architecture and documentation tests and fix the docs, not the tests; regenerate the OpenAPI snapshot, review the diff (new notifications and admin routes, bearer requirements; the hub is not in OpenAPI) and accept it per ADR 0011.
- [ ] **Step 5: Verify the template:** `dotnet build -c Release` (0 warnings), `dotnet format --verify-no-changes`, `bash build/scripts/template-smoke.sh`, the licence job's command. Expected: all exit 0.
- [ ] **Step 6: Run by hand:** `docker compose up -d`, start the API, register through Scalar with `locale` `ms`, read the Malay email in Mailpit (`http://localhost:8025`), confirm, log in, change the password, see the in-app item through `GET /api/v1/notifications/inbox`. Record anything surprising in the PR description.
- [ ] **Step 7: Commit** `docs(notifications): document the module and verify the full flow`, push `feature/notifications` (after A3) and open one PR titled `feat(notifications): add the notifications module`.

---

## Part C: Follow-up after the tasks merge

- [ ] **C1: Native review** of the `ms` and `zh-Hans` templates and error messages (review 8.3), together with Plan 2's C2.
- [ ] **C2: Plan 7's `notifications` template switch** must be reconsidered: Auth now depends on Notifications to send confirmation and reset emails. Recommended: drop the switch, or let it remove only the in-app channel and the hub.
- [ ] **C3: Deferred from blueprint 10** (D17): scheduling and cancellation, digest, priorities below `Critical` in real types, push (FCM), SMS, webhooks, provider callbacks and delivery tracking beyond `Sent`, suppression list, bulk sends, attachments, fallback channels, admin template editor with preview and test send, global mute, a synced `NotificationTypes` table. Each becomes a backlog item with its blueprint section.
- [ ] **C4: Mark row 3** of `docs/blueprint-review.md` Section 6 as done, then start Plan 4 (MFA).

## Part D: What the next plans inherit from this one

- **Plan 4 (MFA):** adds `auth.mfa_changed` (and `auth.new_device_login` if trusted devices land) as Auth Contracts events plus catalog entries and templates in all three cultures; the completeness tests fail until every template exists.
- **Plan 5 (Platform):** Redis backplane for SignalR (D13) and Redis as the ticket store if wanted; cleanup jobs: `notify.InboxMessages` and processed outbox rows after 7 days, `notify.Deliveries` and `notify.Notifications` after 180 days (blueprint 11.7), consumed or expired `notify.HubTickets` hourly; Hangfire may host the delivery loop behind the same `DeliveryDispatcher`.
- **Plan 6 (Delivery):** encrypting the Data Protection key ring now also protects `notify.Notifications.ProtectedData` (ADR 0020); the image needs ICU and `tzdata` for cultures and IANA zones; the `migrate` mode includes `NotificationsDbContext` automatically.
- **Plan 7 (Packaging):** C2; the `modulith-module` item template should offer an optional `INotificationTypeSource` with a template folder, which is when `TemplateName.Modules.Notifications.Contracts` is created (D1).

## Part E: Traceability

| Spec item | Delivered by |
|---|---|
| Review 2.1 per-module outbox with consumer tracking, generic dispatcher reused | Task 1 (message context), Task 4 |
| Review 2.2 integration events, idempotent inbox, eventual consistency | Tasks 1, 3, 4, 10; ADR 0018 |
| Review 2.6 Dapper reads filter by hand | Tasks 3, 13, 16 (filter proofs) |
| Review 2.8 Notifications replaces the minimal `IEmailSender` | Task 12 |
| Review 2.10 SignalR token in the query string | Task 15; ADR 0021 |
| Review 6 row 3: types, Scriban templates, email + in-app, worker with retries and dead-letter, preferences, quiet hours, security notifications wired to auth events | Tasks 5, 8, 9, 11-15, 10 |
| Review 8.2 recipient locale, `zh-Hans` → `en` fallback, never the request culture, IANA time zones | Tasks 5, 8, 10 |
| Review 8.1 I7 background work never inherits the OS culture; I9 completeness in CI | Tasks 5, 8 |
| Review 11 docs, ADRs, Conventional Commits | every task; Task 17 |
| Blueprint 10.1 email (SMTP, Mailpit) and in-app (SignalR + table) channels | Tasks 11, 12, 15 |
| Blueprint 10.2 architecture (event → request → rows → worker → provider), corrected by review 2.2 | Tasks 4, 10, 11 |
| Blueprint 10.3 `NotificationRequest`, channel abstraction (no `CancelAsync`, D17) | Tasks 10, 11 |
| Blueprint 10.4 types in code, templates per type × channel × language with layout, preferences, quiet hours, priorities (`Critical` bypass), dedup, retries 1m/5m/15m/1h/6h max 5, permanent failure, delivery status, admin resend | Tasks 5, 6, 8-11, 14, 16 (digest, scheduling, fallback, bulk, attachments, suppression, webhooks deferred: C3) |
| Blueprint 10.5 endpoints `/inbox`, `/inbox/unread-count`, read, read-all, `/preferences`, hub; admin deliveries | Tasks 13-16 (devices and provider webhooks deferred) |
| Blueprint 10.6 built-in security types | Task 9 (`auth.login_otp`, `auth.new_device_login`, `auth.mfa_changed` with Plan 4) |
| Blueprint 11.3 notify schema (subset, D7, D9) and 11.4 inbox | Tasks 1, 7 |
| Blueprint 12.2 Notifications tests: rendering, preference filtering, quiet hours, retry and dead-letter, idempotency | Tasks 6, 8, 10, 11, 14, 17 |
| Blueprint Phase 4 items in scope | Tasks 5-17 |
| Plan 2 Part D: replace `IEmailSender`/`AuthEmails`, new Auth Contracts, consume the auth events | Tasks 3, 4, 12 |
