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

Removing the module removes the trail: the event then has no consumer, each owning outbox marks its row processed, and the changes are not recorded.

## Endpoints

None.

## Domain model

None.

## Error codes

None.

## Events

The module raises no events and has no outbox. It consumes one integration event, published by the outbox of every module that saves an `[Audited]` entity.

| Event | Kind | Raised when | Handlers |
|---|---|---|---|
| `EntityChangesRecordedIntegrationEvent` (`Application.Common`) | Integration | An owning module's outbox dispatcher publishes the change set of one save: `Id` (the outbox message id, which is the change set id), `OccurredAt`, `UserId`, `SessionId`, `TraceId`, `Module` and one `EntityChange` per changed entity (`EntityType`, `EntityId`, `Action`, `Changes`). | `EntityChangesRecordedIntegrationEventHandler` (`Application/Trails/Record/`) |

The consumer:

1. Asks the inbox whether it has processed the event's `Id` (its consumer name is its full type name); if so, it returns.
2. Adds one `AuditTrail` per change, with `ChangeSetId` = the event's `Id` and the event's time, user, session, module and trace id on every row, records the inbox row, and saves once, so the rows and the inbox row commit together.
3. If that save fails on the inbox key (`Inbox<AuditDbContext>.IsDuplicate`), a delivery that raced this one wrote the rows: the pending changes are discarded and the event counts as processed.

Any other failure is thrown, so the publishing module's outbox retries the message on its schedule and, after `Outbox:MaxAttempts`, abandons it and counts it (ADR 0007, ADR 0028): the trail is late, not lost. A change set with many changes is one save with no cap, so a bulk operation produces one large set. Registration order among the modules does not matter: the consumer is found by event type.

## Configuration

None of its own. The inbox rows follow the shared `Retention:Inbox` setting.

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

## Observability

The readiness check `audit` (tag `ready`, part of `/health/ready`) checks that `AuditDbContext` can reach the database. The module has no meter and no log messages of its own; a failed consumer surfaces in the publishing module's outbox logs and its `platform.outbox.abandoned` gauge.

## Testing

- Integration tests, `tests/TemplateName.IntegrationTests/Audit/`: `AuditPersistenceTests` checks the migrated schema (tables, column types, the `ISJSON` check refusing text that is not JSON, the four indexes by column list), that `audit.trails.view` is seeded and held by `SuperAdmin` and `Admin`, and the `audit` readiness check. `AuditConsumerTests` publishes change sets through the host's `IIntegrationEventPublisher`: one row per change with the event's id, time, user, session, module and trace id; a duplicate delivery writing the rows once; and a delivery that races past the inbox check being dropped as processed.
- Architecture tests: the module is in `Assemblies.Modules`, so the layering, boundary, naming and documentation rules apply to it.

## Changelog

Changes to this module are the [`CHANGELOG.md`](../../CHANGELOG.md) entries with scope `audit` (release-please prints them as **audit:**). To list them from git: `git log --oneline -E --grep='^[a-z]+\(audit\)!?:'`.
