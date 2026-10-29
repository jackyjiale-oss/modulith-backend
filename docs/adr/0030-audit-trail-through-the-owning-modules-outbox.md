# 0030. Audit trail through the owning module's outbox

- Status: Accepted
- Date: 2026-10-28
- Deciders: Lee Jia Le

## Context
Until Plan 5b the only traces of a change were two. The `CreatedBy/UpdatedBy/DeletedBy` stamps on a row say who wrote it last, with no values. `auth.AuthAuditLogs` says who signed in, was locked out or acted as an administrator, with address and user agent, but no field values. An administrator who asks "what did this role look like before the change" or "what did this person change yesterday" cannot answer from either, short of reading the database.

The answer has to be captured where the change happens, in the same transaction, in every module's `DbContext`; yet a context may not write another module's schema (the module rules: one schema per owner and no write to another module's tables, see [Data ownership](../architecture/overview.md#data-ownership)), and one `SaveChanges` is one transaction in one context. Two follow-ups also stood open: the blueprint's "12 months hot, then archive" names no archive target, and ADR 0028 left "an archive target for audit logs, decided with the Audit module".

The trail is for administrator forensics. It is not a compliance record: no hash chain, no signed export, no per-user history view, and no tenant or impersonation column (the template has neither concept).

## Options considered

**How a change reaches the Audit module (D5 of the Plan 5b design).**
1. **A direct write from an interceptor into `audit.AuditTrails`.** One transaction and no delivery machinery, but a cross-schema write that breaks the module rule and couples every module's context to the Audit schema, its migrations and its availability.
2. **Temporal tables** on the audited tables. No code to write, but only the old values: no actor, no session, no trace id, no key text to filter by; SQL Server specific (ADR 0003 allows that, but nothing else in the template depends on it); and the history lives in the owner's schema, so Audit could not offer one query over modules.
3. **The owning module's outbox, with a shared event type.** An interceptor adds one outbox row to the same save; the owner's dispatcher publishes an integration event; the Audit module consumes it through its inbox. Atomic capture and at-least-once delivery from existing machinery (ADR 0007, ADR 0018), exactly-once recording from the inbox, and Audit never reads or writes another schema.

**The overlap with `auth.AuthAuditLogs` (D2).**
1. **Move the Auth log into Audit.** One log, but Auth would depend on Audit or publish every security event as an integration event: a large change to a module that works, and its endpoint (`GET /admin/auth/audit-logs`), its events and its retention would change.
2. **Have Audit read Auth's log.** Forbidden by the module rules.
3. **Keep both logs.** They answer different questions ("who tried to sign in from where" and "what did this role look like before"). Audit records field changes only, including those of Auth's own entities.

**What happens to old trails (D3).**
1. **A file archive** (JSON lines to disk or blob) **or an archive table in the Audit schema.** Both add a writer, a restore story and a failure mode to a delete job, for a need no consumer has yet.
2. **A delete window only, off by default.** `Audit:Retention:Trails`, like `Auth:Retention:AuditLogs` (ADR 0028). The operator who must archive does it in the database before the window passes.
3. **Keep everything and say nothing.** The table would grow without a bound.

**Which entities are audited (D4).**
1. **Opt-out**, every entity unless marked. Safe for coverage, unsafe for secrets: every new column would be audited until someone remembered.
2. **Opt-in with `[Audited]`** and per-property `[AuditIgnore]` and `[AuditMask]`. A forgotten attribute loses a trail where it was wanted; a forgotten opt-out would write a secret into a table that outlives the data. The cost is one attribute per entity, visible in review.

## Decision
We chose the outbox with a shared type, both logs, a delete window with no archive, and opt-in (decisions D2 to D7 of the Plan 5b design).

- **Capture.** `AuditTrailInterceptor` (`Infrastructure.Common/Auditing`, in every module context, after `SoftDeleteInterceptor` and before `DomainEventsToOutboxInterceptor`) reads the change tracker for entities of a type marked `[Audited]` and adds one outbox row to the same save, holding an `EntityChangesRecordedDomainEvent` with one `EntityChange` per changed entity (`EntityType`, `EntityId`, `Action`, `Changes`) and the user, session, trace id, time and module (the context's default schema). A save that changes nothing audited adds no row. The row is added before the save and the interceptor's own failure fails the save, so an audited change is never saved without its record.
- **Transport.** A handler registered once for the host, `EntityChangesRecordedDomainEventHandler`, runs from every module's outbox and publishes `EntityChangesRecordedIntegrationEvent`, with `Id` and `OccurredAt` taken from `IOutboxMessageContext` (so a retry republishes the same id, ADR 0018). The event lives in `Application.Common`, not in a module's contracts, because every module's outbox publishes it and the Audit module must not reference every module's contracts. `OutboxEventTypes<TContext>` always knows the domain event type, so the dispatcher can read the row whatever the module's assembly holds. The record's name is stored in outbox rows, so renaming it would orphan pending rows.
- **Audit module.** One project, `TemplateName.Modules.Audit`, with no `.Contracts` project (D7: it offers nothing to other modules yet, like Notifications), no `Resources/` folder and no error codes. Its consumer writes one `audit.AuditTrails` row per change, with the inbox row in the same save, so a set is recorded once. `GET /api/v1/admin/audit/trails` (permission `audit.trails.view`) lists them, filtered and cursor-paged (ADR 0010). `AuditTrailsCleanup` deletes trails older than `Audit:Retention:Trails` in bounded batches (ADR 0028); the template ships no archive, so this closes the follow-up of ADR 0028.
- **Keys.** The key of an audited entity is application-generated (`ValueGeneratedNever`, `SequentialGuid.Create`, ADR 0004), because the key of an `Added` entity must be known when the interceptor runs; an architecture test enforces it, and an `Added` entry with a temporary store key fails the save naming the type. `EntityId` is the CLR key value in invariant culture; a composite key is `Name=value;Name=value`.
- **Secrets.** A secret never reaches the trail: `[AuditIgnore]` leaves a property out, `[AuditMask]` records that it changed with `"****"` in place of both values. An architecture test fails the build when an audited property whose name ends in `Hash`, `Secret`, `Token`, `Salt`, `Key` or `Stamp`, or is `Password`, carries neither. The list is small on purpose; a name outside it (`Passphrase`) needs the attribute by review. Personal data (an email, a display name) is recorded in clear text, because masking it would make the trail useless for its main case; the permission and the retention setting are the controls (D6).
- **What a row holds.** The stored (provider) value, so a value converter applies; `Created` and `Deleted` list every audited property that is not null, `Updated` only the changed ones with `old` and `new`, and a soft delete or restore is its own action. The `IAuditable` and `ISoftDeletable` columns and every store-generated value (a `RowVersion`, a column with `HasDefaultValue` or `HasDefaultValueSql`) are skipped by rule. A text value over 4,000 characters is cut and ends in an ellipsis.
- **A retried save.** If a save fails, the audit row stays tracked next to the still-changed entities, so a retry on the same context would add a second row. The interceptor remembers the row it added for each context: a retry replaces its content, or removes it when nothing audited is left, and it forgets the row when the save succeeds.
- **First audited entities:** `LeaveRequest` (Sample) and `User`, `Role`, `RolePermission` and `UserRole` (Auth). Auth's `PasswordHash`, `SecurityStamp`, `NormalizedEmail`, `NormalizedName`, `LastLoginAt` and `AccessFailedCount` are ignored.

## Consequences
- Positive: capture is atomic with the change and needs no cross-schema write; the Audit module references no other module and no module references it; a module opts in with one attribute and its own context; the delivery, retry, dead-letter and inbox behaviour are the existing, tested ones.
- **Every audited save adds an outbox row and a message to dispatch.** A bulk operation produces one change set with one entry per changed entity and no cap, so a very large save writes a large row, and the consumer writes it in one save. A value over 4,000 characters is cut (at a surrogate pair boundary it can be 3,999). A `byte[]` property is not truncated, so a binary column gets `[AuditIgnore]`.
- **Tests that count a module's outbox rows now see audit rows.** The integration tests filter them with `AuditOutboxFilter` where they count or take their own events, and do not where they scan the outbox for secrets.
- **Not recorded: raw statements.** `ExecuteUpdate`, `ExecuteDelete` and raw SQL bypass the change tracker, so the interceptor never sees them: Auth's atomic failed-sign-in count, the token-consuming statements and every cleanup delete are not audited, and the Auth security log covers the first two. A save that changes only an ignored property (a sign-in only changes `LastLoginAt`) writes no trail.
- **No tamper evidence.** A database administrator can change or delete the rows; there is no hash chain. A compliance trail is a different design, and this one would be its input.
- **No owned or complex types.** The interceptor reads scalar properties; an architecture test fails the build for an audited entity with an owned or complex property.
- **Removing the module removes the trail.** Without the Audit module the integration event has no handler, the owner's outbox marks the row processed and the changes are not recorded.
- **A failing consumer delays the trail, it does not lose it.** The owner's outbox retries on its schedule and counts the message as abandoned after `Outbox:MaxAttempts` (`platform.outbox.abandoned`); an abandoned message is never deleted, so an operator can re-queue it (ADR 0028).
- **The trail holds personal data.** An email and a display name are in clear text, kept until `Audit:Retention:Trails` passes; with that setting `null`, which is the default, for ever.
- **The permission reads across modules.** A holder of `audit.trails.view` reads every audited property of every module, whatever that module's own read permissions say: the `Admin` role holds no Sample permission, yet reads every leave request's `Reason` through the trail. Marking an entity `[Audited]` widens what the holders see, so the permission is handed out accordingly. Therefore it is not `AdminDefault` (`AdminDefault: false` in `AuditPermissionSource`): only `SuperAdmin`, which holds every permission, has it by default, and granting it to another role is a deliberate choice. This was decided in the final review of the module; the first design flagged it `AdminDefault`.
- **The first rows have no user.** The Auth seeder's first run writes the seeded roles and their grants with a null `UserId` and `SessionId`; so does any background work.
- Follow-up actions: a reader API for other modules, added with its first consumer; a column and a filter for tenant or impersonation when the template gains the concept; an archive job behind `IRecurringJob` ([ADR 0026](0026-recurring-jobs-on-our-own-scheduler.md)) for a deployment that needs one.
