# Plan 5b: Audit module — design

- Status: Approved 2026-10-28 (decisions D1 to D7: D1 to D6 were agreed section by section in the design session, D7 is new and flagged in section 10)
- Date: 2026-10-28
- Roadmap row: 5b of `docs/blueprint-review.md` Section 6 (review 2.3, blueprint 8.9, 11.5)
- Builds on: Plans 1–5, all merged (main `ba6957f`)

## 1. Intent

**Outcome.** An administrator can answer "who changed this record, when, and from what value to what" for the records that matter, without reading the database. Today the only trace is the `CreatedBy/UpdatedBy/DeletedBy` stamps on the row itself (the last writer only) and `auth.AuthAuditLogs` (sign-in and administration events, no field values).

**Success looks like.**

1. A module marks an entity `[Audited]`; from then on every save that changes an audited property writes one change set, in the same transaction as the change, through the module's own outbox. No module writes another module's tables.
2. The Audit module consumes the change set through its inbox, exactly once per set, and stores one row per changed entity.
3. `GET /api/v1/admin/audit/trails` answers the question above, filtered and cursor-paged, behind its own permission.
4. A secret never reaches the trail: ignored or masked by attribute, and an architecture test fails the build when a secret-looking property is audited without one.
5. Nothing deletes a trail unless the operator sets `Audit:Retention:Trails`; the template ships no archive. This closes the follow-up of ADR 0028.
6. Nothing here changes an existing route or response shape.

**Said by you (the roadmap and docs):** "audit trail via outbox" (review 2.3 amendment), a spec first that settles the cross-module change-record contract, the overlap with `auth.AuthAuditLogs` and the archive target (review row 5b), and the open follow-up in ADR 0028.

**Assumed by me:** SQL Server stays the only database; the entities audited first are the five in section 6.5; no multi-tenancy, so no tenant column; the Audit schema lives in the same database as the other modules.

## 2. Non-goals

- **No compliance hash chain, no tamper evidence, no user-facing history.** This is admin forensics. A database administrator can change the rows.
- **No tenancy or impersonation columns.** The template has neither; a later plan adds a column and a filter when it adds the concept.
- **No archive.** The template deletes after a window or keeps; an operator who must archive does it in the database (a scheduled copy before the retention, a partition switch, or a backup).
- **No owned or complex types** in an audited entity (an architecture test fails the build). The interceptor reads scalar properties only.
- **No auditing of `ExecuteUpdate`, `ExecuteDelete` or raw SQL.** They bypass the change tracker, so the interceptor never sees them. Auth's atomic failed-sign-in count and the token-consuming statements are of this kind, and the Auth security log covers them. The module documentation states this.
- **No auditing of what the module itself stores.** `AuditDbContext` has no audited entity; a trail row is never itself audited.

## 3. Decisions

| # | Decision | Why | Rejected, and cost if you disagree |
|---|---|---|---|
| D1 | **Purpose is admin forensics.** The trail records field changes: entity, key, actor, session, time, old and new values. | It is what an administrator asks for. A hash chain or a per-user history view would add a second product with its own security and retention questions. | A compliance trail (hash chain, signed export) is a different design; this one would then be its input, not its replacement. |
| D2 | **Keep both logs.** `auth.AuthAuditLogs` stays the security-event log (sign-in, lockout, token reuse, administrator actions, with IP and user agent). Audit records field changes only, including those of Auth entities (`User`, `Role`, `RolePermission`, `UserRole`). | The two answer different questions: "who tried to sign in from where" and "what did this role look like before the change". Moving the Auth log would break Auth's own events, its admin endpoint (`GET /admin/auth/audit-logs`) and its retention for no gain. | **Moving the Auth log into Audit:** one log, but Auth would then depend on Audit or publish every security event as an integration event; a large change to a module that works. **Having Audit read Auth's log:** forbidden by the module rules. |
| D3 | **Delete window only.** `Audit:Retention:Trails` is `null` (off) by default, like `Auth:Retention:AuditLogs`. When set, a daily job deletes older trails in batches (ADR 0028). The template ships no archive. | "12 months hot, then archive" in the blueprint names no archive target. A file or table archive is code to own, test and secure for a need no consumer has yet. | **A file archive (JSON lines to disk or blob)** or **an archive table in the Audit schema:** both add a writer, a restore story and a failure mode to a delete job. An operator who needs one can add a job behind `IRecurringJob` (ADR 0026). |
| D4 | **Opt-in with `[Audited]`.** `[AuditIgnore]` and `[AuditMask]` apply to properties. | A forgotten opt-out would write secrets or high-frequency noise into a table that outlives the data. Opt-in costs one attribute per entity and is visible in review. | **Opt-out (everything audited unless marked):** safe by default for coverage, unsafe for secrets; every new column would be audited until someone remembers. |
| D5 | **Transport is a shared outbox type, not a direct sink.** `AuditTrailInterceptor` adds one outbox row per save holding an `EntityChangesRecordedDomainEvent`; a shared handler turns it into an `EntityChangesRecordedIntegrationEvent`; the Audit module consumes it through its inbox. | The interceptor lives in every module's context, and a context may not write `audit.*` (module rule; one `SaveChanges` is one transaction in one context, review 2.1). The outbox gives atomic capture and at-least-once delivery; the inbox makes the write exactly once (ADR 0007, ADR 0018). | **A direct write from the interceptor into `audit.AuditTrails`:** one transaction, but a cross-schema write that breaks the module rule and couples every module's context to the Audit schema. **Temporal tables:** old values only, no actor, no session, SQL Server specific, and the history lives in the owner's schema. |
| D6 | **PII is clear text.** Only secrets are ignored or masked. | An administrator needs the real email and display name to read a change. Masking them would make the trail useless for the main case. | **Masking all personal data:** unreadable trail. The retention setting and the permission are the controls; the module documentation says the trail holds personal data. |
| D7 | **No `.Contracts` project.** Audit ships as one project, like Notifications. | Its input event lives in `Application.Common`, and nothing in it is offered to other modules, so a contracts project would be empty (`Assemblies.cs`: one entry to `Contracts` "when it has a contracts project"). A reader API for other modules is added with its first consumer. | **An empty `.Contracts` project:** satisfies the "two projects" sentence in ADR 0001 and the module `CLAUDE.md`, and costs a project that holds nothing. Flagged in section 10. |

## 4. Building blocks

### 4.1 `SharedKernel/Auditing`

- `[Audited]` (class attribute, `AttributeTargets.Class`, not inherited), `[AuditIgnore]` (property: never written) and `[AuditMask]` (property: a change is recorded, the values are replaced by `"****"`). They live in `SharedKernel` because a module's `Domain` may depend on nothing else (`LayeringTests`).
- `EntityChangeAction`: `Created`, `Updated`, `Deleted`, `SoftDeleted`, `Restored`.
- `EntityChange(string EntityType, string EntityId, EntityChangeAction Action, string Changes)`: a public sealed record. `EntityType` is the CLR type name without namespace (`LeaveRequest`); `Changes` is JSON object text (section 5.3).
- `EntityChangesRecordedDomainEvent(Guid? UserId, Guid? SessionId, string? TraceId, string Module, IReadOnlyList<EntityChange> Changes) : IDomainEvent`: the payload of the outbox row. No aggregate raises it.

### 4.2 `Application.Common/Auditing`

- `EntityChangesRecordedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid? UserId, Guid? SessionId, string? TraceId, string Module, IReadOnlyList<EntityChange> Changes) : IIntegrationEvent`. It sits in `Application.Common`, not in a module's contracts, because every module's outbox publishes it and the Audit module must not reference every module's contracts (review row 5b). `Id` is the outbox message id, which is also the change set id.

### 4.3 `Infrastructure.Common/Auditing`

- `AuditTrailInterceptor : SaveChangesInterceptor`, registered like the other interceptors. Section 5 is its behaviour.
- `EntityChangesRecordedDomainEventHandler : IDomainEventHandler<EntityChangesRecordedDomainEvent>` maps the domain event to the integration event with `Id = IOutboxMessageContext.MessageId` and `OccurredAt = IOutboxMessageContext.OccurredAt`, and publishes it through `IIntegrationEventPublisher`. `AddInfrastructureCommon` registers it once for the host (`TryAddEnumerable`); every module's dispatcher resolves it from the scope it creates for a message, so no module registers it. Its record in `OutboxMessageConsumers` is the handler's full name, as for every handler.
- The interceptor adds an `OutboxMessage` (`Type` = the record's `FullName`, `Content` = its JSON, `Id` = `SequentialGuid.Create(now)`) to the context it saves. A context with an audited entity but no `ApplyOutbox()` mapping fails its first save with an `InvalidOperationException` naming the context; an integration test asserts that every context with an audited entity maps the outbox (Auth and Sample do).

### 4.4 `OutboxEventTypes<TContext>`

The registry is built from the module's assembly. Its constructor also adds `typeof(EntityChangesRecordedDomainEvent)` under its `FullName`, so the dispatcher can deserialize the row whatever the module's assembly holds. A type name missing from the registry stays a message failure, as today, so the record is named and kept stable like a published contract: renaming it would orphan pending rows.

### 4.5 Interceptor order (`ModuleDbContextExtensions`)

`AuditableEntityInterceptor`, `SoftDeleteInterceptor`, `AuditTrailInterceptor`, `DomainEventsToOutboxInterceptor`. Audit runs after the soft-delete conversion, so a soft delete is already an update of `IsDeleted` and the stamps are already set (and can be skipped by rule instead of guessed). It runs before the domain-events interceptor, so both kinds of outbox rows are added in the same save as plain tracked entities. The comment above `AddInterceptors` says so.

## 5. Change capture

### 5.1 What the interceptor looks at

The entries of the saving context's `ChangeTracker` whose CLR type has `[Audited]` (decided once per type and cached) and whose state is `Added`, `Modified` or `Deleted`. `OutboxMessage` and every other infrastructure row are not `[Audited]`, so they are never visited. It runs in the sync and async hooks like its siblings.

### 5.2 Rules

| Case | Action | `Changes` holds |
|---|---|---|
| Added | `Created` | every audited property that is not null, as `{"new": value}` |
| Modified, `IsDeleted` false to true | `SoftDeleted` | the other audited properties that changed (usually none, so `{}`) |
| Modified, `IsDeleted` true to false | `Restored` | likewise |
| Modified, no flip | `Updated` | only the audited properties whose value changed, as `{"old": a, "new": b}` |
| Deleted (a hard delete) | `Deleted` | every audited property that is not null, as `{"old": value}` |

- A property is **audited** when the entity is `[Audited]`, the property is a scalar mapped by EF, it has no `[AuditIgnore]`, and it is not skipped by rule.
- **Skipped by rule:** the `IAuditable` columns (`CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`), the `ISoftDeletable` columns (`IsDeleted` is the action itself; `DeletedAt` and `DeletedBy` are stamps), and every store-generated value such as a `RowVersion` (its value is not known when the interceptor runs, and it changes on every save).
- **Mask:** a `[AuditMask]` property that changed is written as `{"old": "****", "new": "****"}` (or the one side that exists). The fact of the change is kept, the value is not.
- **Values** are the provider (stored) value, so an EF value converter applies: a `DateOnly` is its string, an enum stored as a string shows its name, a `DateTime` is the UTC value the database holds. An enum with no converter is written by its name, not its number. A value longer than 4,000 characters is cut to 4,000 and ends in `…`.
- **`EntityId`** is the key value in invariant culture; a composite key is `Name=value;Name=value` in key-property order (`RoleId=…;PermissionId=…`). The key of an `Added` entity must be known when the interceptor runs, so every audited entity has an application-generated key (`ValueGeneratedNever`, as `SequentialGuid.Create` gives, ADR 0004). An `Added` entry whose key is a temporary store value fails the save with an `InvalidOperationException` naming the type, and an architecture test finds such a mapping first.
- **No row when nothing audited changed.** A save that changes only `LastLoginAt` (ignored) or `RowVersion` writes no change set and no outbox row.
- **One change set per save.** All audited entries of a save go into one `EntityChangesRecordedDomainEvent`. The order of entries within it is the tracker's and has no meaning. Every trail row of a set has the set's `OccurredAt`.

### 5.3 The `Changes` text

A JSON object keyed by property name:

```json
{ "Name": { "new": "Reviewer" }, "Description": { "new": "Can approve leave" } }
{ "Status": { "old": "Pending", "new": "Approved" }, "ApproverId": { "old": null, "new": "0a7c…" } }
{ "SomeSecret": { "old": "****", "new": "****" } }
```

On `Updated`, a change to or from null is written with `null` (the transition matters). On `Created` and `Deleted` a null property is left out, and an absent key means null. The column is `nvarchar(max)` with an `ISJSON` check, so an operator can query it with `JSON_VALUE`; the API returns it as the same JSON object, not as a string.

### 5.4 Who, when and which request

- `UserId` and `SessionId` come from `ICurrentUser`; both are null for background work (the outbox, the scheduler, the seeder).
- `TraceId` is `Activity.Current?.TraceId.ToHexString()` (null without an activity), as the Auth writer does.
- `OccurredAt` comes from `TimeProvider` (UTC); the interceptor never reads the system clock.
- `Module` is the context's default schema (`auth`, `sample`), which is the owning module by the schema rule (ADR 0003).

### 5.5 A failed save, and a retried save

The audit outbox row is added before the save. If the save fails, EF leaves it tracked as `Added` and the entities still changed, so a retry of `SaveChanges` on the same context would add a second row for the same changes. The interceptor therefore remembers the row it added for each context (a weak association) and, on the next save of that context, **replaces that row's content** instead of adding one; it forgets the row in `SavedChanges`. If the context is discarded, the row goes with it, as the existing remark on `DomainEventsToOutboxInterceptor` says of its own rows. A failure of the interceptor itself fails the save, so an audited change is never saved without its record.

## 6. The Audit module (`TemplateName.Modules.Audit`)

One project, `internal` by default, with the entry point `AuditModule` (`AddAuditModule(configuration)`, `MapAuditEndpoints()`). Folders: `Domain/Trails/`, `Application/` (the consumer, the list query, abstractions, the permission source), `Infrastructure/` (persistence, retention) and `Endpoints/`. It has no `Resources/` folder: it defines no error code, and `TranslationTests` requires a resource set only for a module that does. Registration order in the host does not matter for correctness, because the consumer is resolved by event type.

### 6.1 Data (schema `audit`, `AuditDbContext`)

`audit.AuditTrails` is a plain entity with a `bigint` identity key, append-only, like `AuthAuditLog`. It is not audited, so the app-generated-key rule does not apply to it.

| Column | Type | Notes |
|---|---|---|
| `Id` | `bigint` identity | the unique tie-breaker of the sort |
| `ChangeSetId` | `uniqueidentifier` | the integration event id; all rows of one save share it |
| `OccurredAt` | `datetime2(3)` | UTC |
| `UserId` | `uniqueidentifier` null | |
| `SessionId` | `uniqueidentifier` null | |
| `Module` | `nvarchar(64)` | |
| `EntityType` | `nvarchar(128)` | |
| `EntityId` | `nvarchar(200)` | key text, see 5.2 |
| `Action` | `varchar(16)` | the `EntityChangeAction` name, stored as text |
| `Changes` | `nvarchar(max)` | JSON, `ISJSON` check |
| `TraceId` | `nvarchar(64)` null | |

Indexes (ascending and read backwards for the other direction, `Id` last as the tie-breaker, ADR 0010 P7): `(EntityType, EntityId, OccurredAt, Id)` for "history of this record", `(UserId, OccurredAt, Id)` for "what did this person change", `(ChangeSetId)` for "everything in this save" and `(OccurredAt, Id)` for the unfiltered list and the retention delete. The `module` and `action` filters narrow the rows those indexes return.

`audit.InboxMessages` comes from `ApplyInbox()` and `AddInbox<AuditDbContext>()`, which also registers `AuditInboxCleanup`. The context does not map the outbox: it raises no domain events. The schema, its `__EFMigrationsHistory`, its `ready` health check and its migration follow the other modules (ADR 0003); the first migration is `CreateAuditSchema`.

### 6.2 The consumer

`EntityChangesRecordedIntegrationEventHandler : IIntegrationEventHandler<EntityChangesRecordedIntegrationEvent>`:

1. Ask the inbox whether this consumer has processed `integrationEvent.Id`; if so, return.
2. Add one `AuditTrail` per `EntityChange` (`ChangeSetId = Id`), record the inbox row, save once, so the rows and the inbox row commit together.
3. If the save fails on the inbox key (`Inbox<TContext>.IsDuplicate`), a delivery that raced this one wrote them: clear the tracker and return.

Any other failure is thrown, so the publishing module's outbox retries the message on its backoff schedule, and the abandoned-message gauge counts it after `Outbox:MaxAttempts` (ADR 0007, ADR 0028). The handler only writes rows, as ADR 0018 requires. A set with many changes is one save; there is no cap, and the module page says a bulk operation produces one large set.

### 6.3 The API

`GET /api/v1/admin/audit/trails`, permission `audit.trails.view`. `AuditPermissionSource` declares it (`AdminDefault: true`) and the Auth seeder picks it up like the Notifications permissions; Audit never references Auth. 401 without a token, 403 without the permission (ruling R13).

- Paging (ADR 0010): `pageSize`, `cursor`, `sort` (`occurredAt` only, default `-occurredAt`) and `includeTotalCount`; `Id` is the tie-breaker. A cursor is bound to the filters through the filter hash.
- Filters: `entityType`, `entityId`, `userId`, `changeSetId`, `module`, `action` (one of the `EntityChangeAction` names, compared exactly), `from` and `to` (inclusive, ISO 8601, UTC when written without an offset; `to` not earlier than `from`), validated like the Auth audit log list.
- Response item: `id`, `changeSetId`, `occurredAt`, `userId`, `sessionId`, `module`, `entityType`, `entityId`, `action`, `changes` (a JSON object) and `traceId`.
- Validation failures are the existing `validation.failed` field errors; no new error code (the validator messages are literal, as the Auth list's are).
- The query is a Dapper read handler (ADR 0006) over `[audit].[AuditTrails]`: every filter value is a parameter and the sort column comes from a constant.

### 6.4 Retention

`Audit:Retention:Trails` is a `TimeSpan?`, validated on start (negative is refused); `null` or zero turns the job off, and that is the default. `AuditTrailsCleanupJob` (a `BatchedCleanupJob`, named `AuditTrailsCleanup`, daily) runs `DELETE TOP (@Batch) FROM [audit].[AuditTrails] WHERE OccurredAt < @Cutoff;` in bounded batches (ADR 0028); the `(OccurredAt, Id)` index supports the predicate. The options type follows `AuthRetentionOptions`. A deployment that must keep trails longer than its window archives them from the database first; the module page says so.

### 6.5 First audited entities

Checked against `User.cs`, `Role.cs`, `RolePermission.cs`, `UserRole.cs` and `LeaveRequest.cs` on `ba6957f`:

| Entity | Module | Audited | `[AuditIgnore]` | `[AuditMask]` |
|---|---|---|---|---|
| `LeaveRequest` | sample | `EmployeeId`, `StartDate`, `EndDate`, `Reason`, `Status`, `ApproverId`, soft delete and restore | none (`RowVersion` and the stamps are skipped by rule) | none |
| `User` | auth | `Email`, `EmailConfirmed`, `Status`, `DisplayName`, `Locale`, `TimeZone`, `PasswordChangedAt`, `LockoutEnd`, soft delete and restore | `NormalizedEmail` (derived from `Email`), `LastLoginAt` and `AccessFailedCount` (high frequency; the security log has the sign-ins), `PasswordHash` and `SecurityStamp` (secrets) | none |
| `Role` | auth | `Name`, `Description`, `IsSystem`, soft delete and restore | `NormalizedName` (derived) | none |
| `RolePermission` | auth | `RoleId`, `PermissionId` (composite key; created and hard-deleted) | none | none |
| `UserRole` | auth | `UserId`, `RoleId`, `AssignedBy`, `AssignedAt` (composite key; created and hard-deleted) | none | none |

Notes that matter:

- The design session's list named a `ConcurrencyStamp` on `User`; the entity has none. Its concurrency token is `RowVersion`, which is store-generated and skipped by rule. `PasswordHistory` (`PasswordHistoryEntry`, which holds hashes) is not audited at all.
- `PasswordChangedAt` is audited: it records that the password changed without recording what it changed to. A password change shows as a `PasswordChangedAt` change; an administrator's suspension as `{ "Status": { "old": "Active", "new": "Suspended" } }`.
- Nothing in the first set needs `[AuditMask]`. The attribute is for the first entity that holds a secret whose change is worth proving (a stored API key, for instance), and a unit test exercises it on a test entity.
- The Auth seeder's first run creates the seeded roles and their grants with no user, so the first trail rows have a null `UserId`. That is correct, and the Auth page says so.
- Raw statements (the atomic failed-sign-in count, token consumption, the cleanup deletes) are not audited by design (section 2).

## 7. Errors and failure modes

| Failure | Behaviour |
|---|---|
| The interceptor cannot build a change set (an unreadable mapping, a temporary key) | The save fails with an `InvalidOperationException` naming the entity type; the business change is never saved without its record. |
| The context has an audited entity but no outbox mapping | The first save fails with an `InvalidOperationException` naming the context; found in tests, not in production. |
| The consumer's save fails (database down) | The exception reaches the outbox dispatcher, which retries on its schedule; after `Outbox:MaxAttempts` the owner's message is abandoned and counted by `platform.outbox.abandoned{context}`. Trails are late, not lost, until an operator acts on the dead letter. |
| Two deliveries of one set race | The second save fails on the inbox key and is dropped as processed (ADR 0018). |
| The Audit module is not registered | The integration event has no handler, the publisher completes, the owner's outbox row is marked processed, and the changes are not recorded. The template registers the module; the module page says that removing it removes the trail. |
| An abandoned message older than the outbox retention | The outbox job never deletes an abandoned message (ADR 0028), so an operator can still re-queue it. |

No new error code is introduced. If the review adds one, it goes into the `.resx` files of a new `AuditErrorMessages` in `en`, `ms` and `zh-Hans`, and `Assemblies.ErrorMessageResources` gains the marker.

## 8. Testing

Written first, in folders that mirror the source. Integration tests run against SQL Server through Testcontainers, as in earlier plans.

**Unit** (`tests/TemplateName.UnitTests/`):

- Change extraction for each action: `Created` (non-null audited values), `Updated` (changed values only, old and new, null transitions), `Deleted` (old values), `SoftDeleted` and `Restored` from the `IsDeleted` flip with and without other changes.
- `[AuditIgnore]` and `[AuditMask]`; the secret never appears in `Changes` in either case.
- Skipped columns: the `IAuditable` and `ISoftDeletable` stamps and a `RowVersion` are never in the text.
- Truncation at 4,000 characters; converter values (`DateOnly`, an enum stored as a string, an enum without a converter); a composite key as `A=…;B=…`.
- A save that changes only ignored properties produces no outbox row.
- A retried save replaces the pending row instead of adding a second.
- A temporary store key on an `Added` entity throws with the type name.
- The domain event handler's mapping (`Id` and `OccurredAt` from `IOutboxMessageContext`); the consumer's mapping from event to rows; the list query validator (page size, `action`, the two timestamps, `to` before `from`); the retention options validator.

**Architecture** (`tests/TemplateName.ArchitectureTests/`):

- Every `[Audited]` entity has an application-generated key (no key property is `ValueGeneratedOnAdd`, composite keys included).
- No `[Audited]` entity has an owned or complex property.
- The secret-name rule: an audited property whose name ends in `Hash`, `Secret`, `Token`, `Salt`, `Key` or `Stamp`, or is `Password`, must carry `[AuditIgnore]` or `[AuditMask]` (so `PasswordChangedAt` passes and `PasswordHash` does not).
- The three attributes and the change types are in `SharedKernel`.
- `TemplateName.Modules.Audit` references no other module, and no module references it.
- The new assembly is in `Assemblies.Modules`, so the layering, boundary, naming and documentation rules apply to it.

**Integration** (`tests/TemplateName.IntegrationTests/`):

- Submit and approve a leave request through the API: two change sets appear (`Created`, then `Updated` with `Status` Pending to Approved), with the caller's user and session ids and a trace id, saved in the same transaction as the change.
- Change a role's permissions through the Auth endpoint: `RolePermission` rows are `Created` and `Deleted` with composite ids.
- A save that fails (a concurrency conflict) leaves no trail and no pending audit outbox row.
- A duplicate delivery of one event writes its rows once.
- The endpoint: 401 without a token, 403 without `audit.trails.view`, each filter and two together, cursor paging in both directions, `includeTotalCount`, and a cursor replayed against other filters (`pagination.cursor_mismatch`).
- `AuditTrailsCleanup`: rows either side of the cutoff, a backlog larger than one batch drains across runs, `null` and zero turn it off, and the execution plan of the delete uses `(OccurredAt, Id)`.
- Every context with an audited entity maps the outbox.

`bash build/scripts/template-smoke.sh` runs after the change: the generated project builds with the new module, its tests pass, and the module is renamed with the others.

## 9. Documents and the decision record

Updated in the same change as the code, as the definition of done requires.

- **New:** `docs/modules/audit.md` (from `_template.md`, with operations and the limits of section 2); `docs/adr/0030-audit-trail-through-the-owning-modules-outbox.md`, titled "Audit trail through the owning module's outbox" (the options of D5, with D2 and D3 as part of the decision); a short section on the attributes, the event and the interceptor in `docs/building-blocks/shared-kernel.md`, `application-common.md` and `infrastructure-common.md`.
- **Changed:** `docs/architecture/overview.md` (the module list, the `audit` row of the schema table, the interceptor order in the request flow); `docs/modules/auth.md` and `docs/modules/sample.md` (the entities each audits, that Auth's log and Audit's trail are two logs, and the retention text for `AuditLogs`, which no longer says "until an archive exists"); `docs/adr/0028-retention-policy-and-the-inbox-window.md` (the follow-up is closed by this plan); `docs/README.md` (index); `build/template-content/CLAUDE.md` and `build/template-content/README.md` (the module list and the rule "audit a new entity with `[Audited]`"); `docs/blueprint-review.md` (row 5b done, and review 2.3 points at the ADR).
- **Configuration:** like every other retention key (`appsettings.json` holds none), `Audit:Retention:Trails` is not listed in `appsettings.json`; its default (`null`, off) and its meaning are in the module page.

## 10. Open questions for the review

None block the implementation plan. Two choices are easy to reverse and are listed so they are not silent:

1. **D7, no `.Contracts` project.** This follows Notifications. Say so if you want the empty project to keep the "two projects" rule literal.
2. **The secret-name suffixes in section 8.** The list is deliberately small; a name outside it (for example `Passphrase`) needs the attribute by review, not by test.
