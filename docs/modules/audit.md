# Audit module

## Purpose and boundaries

The Audit module keeps the **audit trail**: who changed which field of which record, when, from which session and request, with the old and new values. It exists for administrator forensics ("what did this role look like before the change", "what did this person change yesterday"). It is one project, `TemplateName.Modules.Audit`, with no `.Contracts` project: its input event lives in `Application.Common`, and it offers nothing to other modules yet.

How a change reaches it:

1. An entity class marked `[Audited]` (`TemplateName.SharedKernel.Auditing`) is saved in its owning module's context.
2. `AuditTrailInterceptor` (in every module context) adds one outbox row to that same save, holding the change set of the save.
3. The owning module's outbox dispatcher publishes it as `EntityChangesRecordedIntegrationEvent` (`TemplateName.Application.Common.Auditing`).
4. This module's consumer writes one `audit.AuditTrails` row per changed entity, once per change set, through its inbox.

What it deliberately does not do:

- It references **no other module** and no other module references it. Every module's outbox publishes the event; the module never reads another schema.
- It does not record `ExecuteUpdate`, `ExecuteDelete` or raw SQL: they bypass the change tracker, so the interceptor never sees them.
- It does not replace the Auth security log. `auth.AuthAuditLogs` keeps sign-ins, lockouts, token reuse and administrator actions with address and user agent; the trail keeps field changes, including those of Auth's entities.
- It records nothing it stores itself: no entity of `AuditDbContext` is audited.
- The trail holds personal data in clear text (an email, a display name); only secrets are left out or masked by the owning entity's `[AuditIgnore]` and `[AuditMask]`.
- It is not a compliance record: no hash chain, no signed export, no tamper evidence (a database administrator can change the rows), no user-facing history view, and no tenant or impersonation column (the template has neither concept).

Removing the module removes the trail: the event then has no consumer, each owning outbox marks its row processed, and the changes are not recorded.

Decisions: [ADR 0030](../adr/0030-audit-trail-through-the-owning-modules-outbox.md) (the trail goes through the owning module's outbox; both logs are kept; a delete window and no archive), [ADR 0007](../adr/0007-per-module-outbox.md) and [ADR 0018](../adr/0018-in-process-integration-events-with-inbox.md) (the outbox and the inbox it is built on) and [ADR 0028](../adr/0028-retention-policy-and-the-inbox-window.md) (retention; its archive follow-up is closed by ADR 0030).

Code: `src/Modules/Audit/TemplateName.Modules.Audit/`. The host calls `AddAuditModule(configuration)` (after `AddInfrastructureCommon`, at any position among the modules) and maps `MapAuditEndpoints()` on the `/api/v1` group. The module has no `.Contracts` project (nothing in it is offered to other modules yet; a reader API is added with its first consumer), no `Resources/` folder and no error code. The change capture that feeds it is in the building blocks: the attributes and the change set types in [`shared-kernel`](../building-blocks/shared-kernel.md#auditing), the integration event in [`application-common`](../building-blocks/application-common.md#auditing) and the interceptor and the handler that publishes it in [`infrastructure-common`](../building-blocks/infrastructure-common.md#audit-trail).

## Endpoints

The module has one route, `GET /api/v1/admin/audit/trails` (`Endpoints/AdminAuditTrailEndpoints.cs`, tag `Audit administration`, operation `ListAuditTrails`). It requires a signed-in caller with the permission `audit.trails.view` (ruling R13): 401 `http.401` without a valid token, 403 `http.403` without the permission. `SuperAdmin` and `Admin` hold it by default; the Auth seeder picks it up from `AuditPermissionSource`.

| Method | Route | Description | Success | Errors |
|---|---|---|---|---|
| `GET` | `/api/v1/admin/audit/trails` | The audit trail, cursor-paged (ADR 0010: `pageSize` 1 to 100, `cursor`, `sort` = `occurredAt`, default `-occurredAt` (newest first), `includeTotalCount`). `Id` is the unique tie-breaker, so lines written at the same instant keep their insertion order across pages. Filters, combined with AND, each left out when blank: `entityType`, `entityId` and `module` (equality, compared by the database, so with its collation: case-insensitive on a default SQL Server install), `userId`, `changeSetId`, `action` (one of the `EntityChangeAction` names, `Created`, `Updated`, `Deleted`, `SoftDeleted` or `Restored`; the validator accepts only that exact spelling, so `created` is a 400, which makes `action` the one case-sensitive filter) and `from` and `to` (ISO 8601 date or date-time, both inclusive; a value without an offset is UTC; a date alone is midnight at the start of that day; `to` not earlier than `from`). A cursor is bound to the filters it was issued for. Each item is `{ id, changeSetId, occurredAt, userId, sessionId, module, entityType, entityId, action, changes, traceId }`, where `changes` is a JSON object keyed by property name (`{"Status":{"old":"Pending","new":"Approved"}}`), not a string. A filter combination that matches nothing is an empty page (`items` `[]`, `nextCursor` null). | 200 `{ items, pageSize, nextCursor, previousCursor, totalCount? }` | 400 `validation.failed` (page size, an `action` that is not an `EntityChangeAction` name, a date that is not ISO 8601, `to` before `from`: field errors under `pageSize`, `action`, `from`, `to`), 400 `request.malformed` (`userId`, `changeSetId` or `includeTotalCount` that does not parse), 400 `pagination.invalid_sort`, `pagination.invalid_cursor`, `pagination.cursor_mismatch`; 401; 403 |

The query is a Dapper read (ADR 0006) over `[audit].[AuditTrails]`: every filter value is a SQL parameter and the sort column comes from a constant. The unfiltered list reads `IX_AuditTrails_OccurredAt_Id` in order, the history of one record (`entityType` and `entityId`) seeks `IX_AuditTrails_EntityType_EntityId_OccurredAt_Id`, and `userId` uses `IX_AuditTrails_UserId_OccurredAt_Id`, each in either direction with no sort operator; `changeSetId` uses `IX_AuditTrails_ChangeSetId`. `module`, `action` and the dates are checked on the rows an index returns. There are no new error codes and no resource files: validation messages are literal, as the Auth audit log list's are, and the date parsing (`AuditTrailTimestamp`) behaves like the Auth list's because this module may not reference Auth.

**Known gap in the OpenAPI document.** `changes` is described as an any-JSON-value schema (`{}`, the `JsonElement` component of the snapshot), not as `type: object`, because the response record carries it as a `JsonElement`. At runtime it is always a JSON object (`CK_AuditTrails_Changes_IsJson` only checks that the text is JSON, and the interceptor always writes an object); a client generated from the document should treat it as a free-form object.

**Upgrade effect on assigning `Admin`.** `audit.trails.view` is flagged `AdminDefault`, so `Admin` receives it in the seeding run that first inserts the code, which on an install that already runs is the first start after the upgrade. Giving a user a role needs the actor to hold every permission the role grants (`auth.permission_grant_not_allowed`, [Auth: User administration](auth.md#user-administration)), so after the upgrade an actor who has `auth.user.assign_roles` but not `audit.trails.view` can no longer assign the `Admin` role (nor create an account with it) until that permission is granted to the actor's role. It is the same effect, with the same cure, as for the Notifications permissions ([Notifications: Permissions](notifications.md#permissions)).

## Domain model

The module has no aggregate and raises no domain event. Its one entity, `AuditTrail` (`Domain/Trails/`), is a plain, append-only record of one changed entity in one save, like `AuthAuditLog`: `AuditTrail.Create(changeSetId, occurredAt, userId, sessionId, module, change, traceId)` copies the change set's fields and one `EntityChange`. It is not audited, and the application-generated-key rule that audited entities follow does not apply to it (its key is a `bigint` identity). What is recorded, and how, is decided by the capture in the building blocks and by the owning entities' attributes; the next section describes it.

## What is recorded

Which entities: only those whose class carries `[Audited]` (not inherited), and in them only scalar properties mapped by EF. Today these are `LeaveRequest` (Sample) and `User`, `Role`, `RolePermission` and `UserRole` (Auth); the owning module's page says which properties each ignores ([Auth](auth.md#audited-entities), [Sample](sample.md#audited-entities)). A module adds an entity by marking it `[Audited]` ([coding conventions](../coding-conventions.md)).

| Case | `Action` | `Changes` holds |
|---|---|---|
| Entity added | `Created` | every audited property that is not null, as `{"new": value}` |
| Entity modified, `IsDeleted` false to true | `SoftDeleted` | the other audited properties that changed (usually none, so `{}`) |
| Entity modified, `IsDeleted` true to false | `Restored` | likewise |
| Entity modified, no flip | `Updated` | only the audited properties whose value changed, as `{"old": a, "new": b}`; a change to or from null is written with `null` |
| Entity deleted (a hard delete) | `Deleted` | every audited property that is not null, as `{"old": value}` |

```json
{ "Name": { "new": "Reviewer" }, "Description": { "new": "Can approve leave" } }
{ "Status": { "old": "Pending", "new": "Approved" }, "ApproverId": { "old": null, "new": "0a7c…" } }
{ "SomeSecret": { "old": "****", "new": "****" } }
```

- **Skipped by rule, never audited:** the `IAuditable` columns (`CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`), the `ISoftDeletable` columns (`IsDeleted` is the action itself; `DeletedAt` and `DeletedBy` are stamps) and **every store-generated value**: a `RowVersion`, and also any column configured with `HasDefaultValue` or `HasDefaultValueSql`, because EF marks it `ValueGenerated.OnAdd` and its value is not known when the save starts. Such a column is **not audited**, even on an audited entity.
- **`[AuditIgnore]`** leaves a property out; **`[AuditMask]`** records that it changed and writes `"****"` in place of the old and the new value (of the side that exists). A save that changes only ignored, stamp or store-generated columns writes no change set.
- **Values** are what the database stores (the provider value), so a value converter applies: a `DateOnly` is its ISO string, an enum stored as a string shows its name (an enum with no converter is written by its name, not its number), a `DateTime` is the UTC value. A text value longer than 4,000 characters is cut and ends in `…` (the result is 4,000 characters, or 3,999 when the cut would split a surrogate pair). `byte[]` properties are written in Base64 and are **not truncated**, so give a binary column `[AuditIgnore]`.
- **`EntityId`** is the CLR key value in invariant culture; a composite key is `Name=value;Name=value` in key-property order (`RoleId=…;PermissionId=…`). The primary-key properties themselves appear in the `Changes` of `Created` and `Deleted` rows (a `RolePermission` row holds `RoleId` and `PermissionId`), because they are audited properties like the others.
- **An audited entity's key must be `ValueGeneratedNever()`** (an application-generated key, `SequentialGuid.Create`, ADR 0004): the key of an added entity must be known when the interceptor runs. An architecture test enforces it. Without it an `Added` entry with a temporary store key fails the save with an `InvalidOperationException` naming the type, and a key that is generated on add would be dropped from the JSON (store-generated values are skipped).
- **One change set per save.** All audited entries of a save go into one change set; every trail row of it has the set's `ChangeSetId` and `OccurredAt`, and the order within it has no meaning. `UserId` and `SessionId` are the caller's (`ICurrentUser`) and null for background work (the outbox, the scheduler, the seeder); `TraceId` is the W3C trace id of the request or job, null without an activity; `OccurredAt` is `TimeProvider` UTC; `Module` is the owning context's default schema (`auth`, `sample`).
- **A retried save does not double-record.** If a save fails, the audit outbox row stays tracked next to the still-changed entities. The interceptor remembers it per context: a retry on the same context replaces its content, or **removes the row when nothing audited is left**, and the row is forgotten once the save succeeds. A concurrency conflict therefore leaves no trail for the change that lost.
- **A capture that cannot be done fails the save**, so an audited change is never saved without its record: a context with audited changes but no default schema throws an `InvalidOperationException` naming the context, and so does one with audited changes but no outbox mapping (`ApplyOutbox()`; every context with an audited entity maps it, and an integration test asserts that Auth's and Sample's do).

## Error codes

None. The module defines no error code and has no `Resources/` folder, which `TranslationTests` require only of a module that defines codes. A bad query is the existing `validation.failed` with field errors (the messages are literal, as the Auth audit log list's are), and the paging errors are the shared `pagination.*` codes.

## Events

The module raises no events and has no outbox. It consumes one integration event, published by the outbox of every module that saves an `[Audited]` entity. The event starts as an `EntityChangesRecordedDomainEvent` (`SharedKernel/Auditing`), which `AuditTrailInterceptor` serializes into an outbox row of the owning module (no aggregate raises it); the handler `EntityChangesRecordedDomainEventHandler` (`Infrastructure.Common/Auditing`, registered once for the host) maps it to the integration event below with the outbox message's id and time. The domain event's type name is stored in outbox rows and is kept stable like a published contract: renaming it would orphan pending rows.

| Event | Kind | Raised when | Handlers |
|---|---|---|---|
| `EntityChangesRecordedIntegrationEvent` (`Application.Common`) | Integration | An owning module's outbox dispatcher publishes the change set of one save: `Id` (the outbox message id, which is the change set id), `OccurredAt`, `UserId`, `SessionId`, `TraceId`, `Module` and one `EntityChange` per changed entity (`EntityType`, `EntityId`, `Action`, `Changes`). | `EntityChangesRecordedIntegrationEventHandler` (`Application/Trails/Record/`) |

The consumer:

1. Asks the inbox whether it has processed the event's `Id` (its consumer name is its full type name); if so, it returns.
2. Adds one `AuditTrail` per change, with `ChangeSetId` = the event's `Id` and the event's time, user, session, module and trace id on every row, records the inbox row, and saves once, so the rows and the inbox row commit together.
3. If that save fails on the inbox key (`Inbox<AuditDbContext>.IsDuplicate`), a delivery that raced this one wrote the rows: the pending changes are discarded and the event counts as processed.

Any other failure is thrown, so the publishing module's outbox retries the message on its schedule and, after `Outbox:MaxAttempts`, abandons it and counts it (ADR 0007, ADR 0028): the trail is late, not lost. A change set with many changes is one save with no cap, so a bulk operation produces one large set. Registration order among the modules does not matter: the consumer is found by event type.

## Configuration

| Key | Type | Default | Meaning |
|---|---|---|---|
| `Audit:Retention:Trails` | `TimeSpan?` | `null` (off) | How long a trail is kept after its `OccurredAt`. A trail goes only when it is strictly older than this; `null` or zero turns the cleanup off, and a negative value is refused when the host starts. |

The default is off (ADR 0028): the template ships no archive, so deleting a trail would lose it, and `appsettings.json` carries no `Audit` key because JSON cannot express `null` with a comment. An operator who sets a window accepts that older trails are gone; one who must archive them does so from the database (`audit.AuditTrails`) before they pass the window. The batch size and the batches per run are the shared `Retention:BatchSize` and `Jobs:MaxBatchesPerRun` settings. The inbox rows follow the shared `Retention:Inbox` setting.

## Data

Schema `audit`, owned by `AuditDbContext` (`Infrastructure/Persistence/`), which is also the module's `IUnitOfWork` and the host of its inbox (`ApplyInbox()`). There is **no outbox** table: nothing in this module raises a domain event. Writes go through `IAuditTrailRepository` and `IInbox` (`Application/Abstractions/`). The rows have no foreign keys: they describe other modules' entities and outlive them. Timestamps are `datetime2(3)` holding UTC (`ApplyDefaultConventions`); `OccurredAt` comes back with offset zero.

| Table | Purpose | Indexes |
|---|---|---|
| `audit.AuditTrails` | One row per changed entity per save (`AuditTrail`), append-only. `Id bigint identity` (the unique tie-breaker of the sort), `ChangeSetId uniqueidentifier` (the integration event id, shared by every row of one save), `OccurredAt datetime2(3)`, `UserId` and `SessionId` (`uniqueidentifier`, null for background work), `Module nvarchar(64)` (the owning context's schema), `EntityType nvarchar(128)` (the CLR type name without namespace), `EntityId nvarchar(200)` (the key as text; a composite key is `Name=value;Name=value`), `Action varchar(16)` (the `EntityChangeAction` name: `Created`, `Updated`, `Deleted`, `SoftDeleted`, `Restored`), `Changes nvarchar(max)` (a JSON object keyed by property name, checked by `CK_AuditTrails_Changes_IsJson`: `ISJSON([Changes]) = 1`, so it can be queried with `JSON_VALUE`), `TraceId nvarchar(64)` (null without an activity). | `PK_AuditTrails` (`Id`); ascending, read backwards for the other direction, `Id` last (ADR 0010): `IX_AuditTrails_EntityType_EntityId_OccurredAt_Id` (history of one record), `IX_AuditTrails_UserId_OccurredAt_Id` (what one person changed), `IX_AuditTrails_ChangeSetId` (everything in one save), `IX_AuditTrails_OccurredAt_Id` (the unfiltered list and a time-based delete) |
| `audit.InboxMessages` | Which consumer has processed which integration event (`InboxMessage`, a building block): `MessageId`, `Consumer nvarchar(400)`, `ProcessedAt`. The daily job `AuditInboxCleanup` (registered by `AddInbox<AuditDbContext>()`) deletes rows processed more than `Retention:Inbox` ago. | `PK_InboxMessages` (`MessageId`, `Consumer`), `IX_InboxMessages_ProcessedAt` (the cleanup) |
| `audit.__EFMigrationsHistory` | Applied migrations of this module. | none |

Migrations (`Infrastructure/Persistence/Migrations/`), in order:

1. `CreateAuditSchema`: creates the schema and the two tables above.

## Background processing

The module runs no hosted service of its own. Its consumer runs when an owning module's outbox dispatcher publishes a change set. `AddInbox<AuditDbContext>()` registers the recurring job `AuditInboxCleanup` (daily), which deletes inbox rows older than `Retention:Inbox`.

`AddRetention` registers the recurring job `AuditTrailsCleanup` (daily, `AuditTrailsCleanupJob`, a `BatchedCleanupJob`). Each run deletes in bounded batches, `DELETE TOP (@Batch) FROM [audit].[AuditTrails] WHERE OccurredAt < @Cutoff;`, where the cutoff is now minus `Audit:Retention:Trails`; the `(OccurredAt, Id)` index serves the predicate. It does nothing while the retention is `null` or zero, which is the default, and a backlog larger than one run drains over the following runs.

## Observability

The readiness check `audit` (tag `ready`, part of `/health/ready`) checks that `AuditDbContext` can reach the database. The module has no meter and no log messages of its own; a failed consumer surfaces in the publishing module's outbox logs and its `platform.outbox.abandoned` gauge.

## Testing

- Integration tests, `tests/TemplateName.IntegrationTests/Audit/`: `AuditPersistenceTests` checks the migrated schema (tables, column types, the `ISJSON` check refusing text that is not JSON, the four indexes by column list), that `audit.trails.view` is seeded and held by `SuperAdmin` and `Admin`, and the `audit` readiness check. `AuditConsumerTests` publishes change sets through the host's `IIntegrationEventPublisher`: one row per change with the event's id, time, user, session, module and trace id; a duplicate delivery writing the rows once; and a delivery that races past the inbox check being dropped as processed. `AuditTrailEndpointTests` calls the route through the host with rows inserted through `AuditDbContext`: 401 and 403 without the permission, each filter alone and combined, an empty page for a combination that matches nothing, `changes` as a JSON object, the default and ascending order, ties on `OccurredAt` kept in `Id` order across pages forward and backward, `includeTotalCount`, a cursor replayed with another filter answering `pagination.cursor_mismatch`, the validation errors (`to` before `from` under `errors.to`), and, on 60,000 seeded rows, the plans SQL Server cached for the unfiltered list and the history of one record (`SqlExecutionPlan.OfCachedStatementAsync`): the expected index and no sort operator. The OpenAPI snapshot holds the route (ADR 0011).
- `AuditTrailsCleanupJobTests` runs the job: registered daily under its name, a trail strictly older than the cutoff deleted and one exactly at it kept, a backlog larger than one batch drained over several runs, `null` and zero deleting nothing (and the host's own configuration keeping everything), and the plan of the job's statement on 200,000 seeded rows seeking `IX_AuditTrails_OccurredAt_Id` without scanning the table.
- Unit tests, `tests/TemplateName.UnitTests/Audit/`: `AuditRetentionOptionsTests` covers the `null` default, a negative value refused on start, and zero and `null` accepted. `ListAuditTrailsQueryValidatorTests` covers the page size, the exact `action` names, ISO 8601 dates (UTC without an offset) and the `to` not earlier than `from` rule.
- `AuditTrailEndToEndTests` (integration, the real host and database, outbox dispatch included) follows real changes into the trail: submitting and approving a leave request writes two change sets (`Created`, then `Updated` with `Status` Pending to Approved) with the caller's user and session id and a trace id; soft-deleting one writes `SoftDeleted`; changing a role's permissions writes `Created` and `Deleted` `RolePermission` rows with composite ids; suspending a user writes the `Status` change and no secret, no `RowVersion`; a sign-in that saves only `LastLoginAt` writes no audit row; the seeder's first run writes trails with a null user and session and a later run that changes nothing audited writes none; two stale approvals of one leave request leave exactly one `Updated` trail (the lost save records nothing); and every context with an audited entity maps the outbox.
- The capture itself is tested with the building blocks: unit tests of `EntityChangeExtractor`, `AuditTrailInterceptor`, the domain event handler and `OutboxEventTypes`, and the integration tests of the interceptor against SQL Server (same-save outbox row, no row for ignored-only changes, a retried save recording once or not at all, dispatch to the integration event with the outbox message id); see [infrastructure-common](../building-blocks/infrastructure-common.md#tests). Because every audited save now adds an outbox row, integration tests that count or take a module's own outbox rows leave the audit rows out with `AuditOutboxFilter`; the tests that scan the outbox for secrets do not, so they prove no secret reaches an audit row either.
- Architecture tests (`AuditingTests`): the audited entities are exactly the five of the first set, with exactly the ignored properties of [Auth](auth.md#audited-entities) and none masked, and each is mapped by a module context; every audited entity has an application-generated key (composite keys included) and no owned or complex property; an audited property whose name ends in `Hash`, `Secret`, `Token`, `Salt`, `Key` or `Stamp`, or is `Password`, carries `[AuditIgnore]` or `[AuditMask]` (so `PasswordChangedAt` passes and `PasswordHash` does not; a name outside the list, such as `Passphrase`, is for review); the attributes and change types are declared in `SharedKernel`; and this module references no other module and none references it. The module is in `Assemblies.Modules`, so the layering, boundary, naming and documentation rules apply to it.

## Operations and known limits

**Failure modes.**

| Situation | What happens |
|---|---|
| The consumer cannot save (the database is down) | The exception reaches the owning module's outbox dispatcher, which retries on its schedule; after `Outbox:MaxAttempts` the message is abandoned and counted by `platform.outbox.abandoned{context}`. Trails are late, not lost, until an operator acts on the dead letter. |
| Two deliveries of one change set race | The second save fails on the inbox key and is dropped as processed (ADR 0018). |
| The Audit module is not registered | The integration event has no handler, the publisher completes, the owner's outbox row is marked processed, and the changes are **not recorded**. Removing the module removes the trail. |
| An abandoned message older than the outbox retention | The outbox job never deletes an abandoned message (ADR 0028), so an operator can still put it back in the queue. |
| The capture cannot build a change set (a temporary key, a missing outbox mapping or default schema) | The business save fails with an `InvalidOperationException` naming the type or the context; found by the architecture and integration tests, not in production. |

**Operating it.**

- **Retention and archive.** `Audit:Retention:Trails` is off by default, so trails are kept for ever; the template ships no archive (ADR 0030). An operator who sets a window loses what passes it, so one who must keep trails longer copies them from `audit.AuditTrails` first (a scheduled copy before the window, a partition switch, or a backup). A deployment that wants an archive job adds one behind `IRecurringJob` ([ADR 0026](../adr/0026-recurring-jobs-on-our-own-scheduler.md)).
- **Personal data.** The trail holds emails and display names in clear text, so the permission `audit.trails.view` and the retention window are its controls. Hand the permission out like any administrator power, and set a retention that matches the data protection policy of the deployment.
- **Querying by hand.** `Changes` is checked with `ISJSON`, so `JSON_VALUE([Changes], '$.Status.new')` works on `audit.AuditTrails`.
- **Dead letters.** A trail that never arrives is an abandoned message in the owning module's outbox (`Error` holds the last exception), not a row here: look at `platform.outbox.abandoned` and the owner's `OutboxMessages` with `ProcessedAt` null.

**Known limits.**

- **Raw statements are not audited.** `ExecuteUpdate`, `ExecuteDelete` and raw SQL bypass the change tracker. In Auth these are the atomic failed-sign-in count, the statements that consume a token and every cleanup delete; the Auth security log covers the first two. A sign-in changes only the ignored `LastLoginAt`, so it writes no trail either (`auth.login_succeeded` is in the security log).
- **No tamper evidence and no user-facing history** (section above); the rows can be changed by anyone with database rights.
- **A bulk operation produces one large change set.** All audited entries of a save are one event with one entry per entity and **no cap**, so a save that changes very many audited entities writes a large outbox row, and the consumer writes it in one save.
- **A long value is cut, a long key is not.** Text values over 4,000 characters are cut (see above); `byte[]` values are not, so binary columns need `[AuditIgnore]`. `EntityType`, `EntityId` and `Module` are not shortened to their columns (128, 200 and 64 characters): a key longer than that would make the consumer's save fail and the message be retried and abandoned.
- **Only scalar properties of audited entities are read.** An owned or complex type fails the architecture test; a column with a default value (`HasDefaultValue`, `HasDefaultValueSql`) is skipped as store-generated.
- **The OpenAPI schema of `changes` is `{}`**, not `type: object` (see [Endpoints](#endpoints)).
- **Filters follow the database collation:** `entityType`, `entityId` and `module` are case-insensitive on a default SQL Server install; only `action` is case-sensitive.
- **No reader API for other modules.** The module has no `.Contracts` project; one is added with its first consumer.

## Changelog

Changes to this module are the [`CHANGELOG.md`](../../CHANGELOG.md) entries with scope `audit` (release-please prints them as **audit:**). To list them from git: `git log --oneline -E --grep='^[a-z]+\(audit\)!?:'`.
