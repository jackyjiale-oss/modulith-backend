# Modulith Backend: Action Plan (Plan 5b: Audit Module)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An administrator can answer "who changed this record, when, and from what value to what" for `[Audited]` entities, through `GET /api/v1/admin/audit/trails`, with no module writing another module's tables.

**Architecture:** `AuditTrailInterceptor` (in every module context, after soft delete, before domain events) writes one outbox row per save holding an `EntityChangesRecordedDomainEvent`; a shared handler republishes it as an `EntityChangesRecordedIntegrationEvent` (id = outbox message id); the new `TemplateName.Modules.Audit` project consumes it through its inbox into `audit.AuditTrails`. Retention is a delete window, off by default.

**Tech Stack:** Plan 5 stack. No new package: unit tests build a SQL Server-provider `DbContext` that never connects, and integration tests use the existing SQL Server Testcontainer.

**Spec:** `docs/superpowers/specs/2026-10-28-audit-module-design.md` (approved 2026-10-28). Read it first; section numbers below refer to it.

## Global Constraints

Everything in Plan 5's Global Constraints applies: Central Package Management, lock files and NuGet Audit; `TimeProvider` only; no abbreviations; `Async` suffix and `CancellationToken` last; no type ending in `Manager`, `Helper`, `Util`, `Utility`, `Impl`, `Dto`, `Settings` or `Info`; Conventional Commits (header at most 72 characters; the scope `audit` is already allowed); `dotnet format --verify-no-changes` before every commit; warnings are errors in Release; the matching `docs/` page updated in the same change; never `--no-verify`; never edit an applied migration.

- **Workflow:** branch `feature/audit-module` created from the tip of `docs/plan-5b-audit` (so the spec and this plan travel with it), in worktree `C:/w/p5bf`. **One commit per task**, one PR at the end, never push to `main`. Commits end with `Co-Authored-By: <the model that wrote it> <noreply@anthropic.com>`.
- **Running tests:** `dotnet build -c Release`, then `dotnet test tests/{Project} --no-build -c Release --filter "FullyQualifiedName~{Name}"`; the full suite is `dotnet test --no-build -c Release` (never `-m:1`). Integration tests need Docker; never restart Docker from a task.
- **Names (spec 4, 6):** attributes `[Audited]`, `[AuditIgnore]`, `[AuditMask]`; `EntityChangeAction` = `Created, Updated, Deleted, SoftDeleted, Restored`; mask text `"****"`; value cap 4,000 characters ending in `…`; composite id `Name=value;Name=value`; schema `audit`, table `audit.AuditTrails`, permission `audit.trails.view`, route `GET /api/v1/admin/audit/trails`, job `AuditTrailsCleanup` (daily), option `Audit:Retention:Trails` (`TimeSpan?`, default `null` = off), first migration `CreateAuditSchema`.
- **Interceptor order (4.5):** `AuditableEntityInterceptor`, `SoftDeleteInterceptor`, `AuditTrailInterceptor`, `DomainEventsToOutboxInterceptor`.
- **No other public route or response shape changes.** The OpenAPI snapshot changes only by the new route (Task 5).
- **Audit module:** one project, no `.Contracts`, no `Resources/` folder, no new error code, references no other module.

## Review Focus

Inputs the spec implies that no single task's happy-path tests cover; each has its test in the named task.

1. **A save that changes only ignored or stamp columns** (a sign-in updating `LastLoginAt`) writes no change set and no outbox row. Task 2 (extractor returns empty), Task 3 (no outbox row), Task 7 (end to end).
2. **An entity key that is a temporary store value** fails the save naming the type, instead of recording a wrong id. Task 2.
3. **A failed save retried on the same context** records the change once. Task 3.
4. **A background save with no signed-in user** (seeder, scheduler) records null `UserId` and `SessionId`, not a crash or a stale id. Task 3 (building block), Task 7 (seeder).
5. **A trail query with a filter combination that matches nothing, or `from` after `to`,** answers an empty page or `validation.failed`, never a 500. Task 5.

---

## Task 1: Shared types

**Files:**
- Create: `src/BuildingBlocks/TemplateName.SharedKernel/Auditing/AuditedAttribute.cs`, `AuditIgnoreAttribute.cs`, `AuditMaskAttribute.cs`, `EntityChangeAction.cs`, `EntityChange.cs`, `EntityChangesRecordedDomainEvent.cs`
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Auditing/EntityChangesRecordedIntegrationEvent.cs`
- Test: `tests/TemplateName.UnitTests/SharedKernel/Auditing/EntityChangesRecordedEventTests.cs`

**Interfaces:**
- Produces: the types of spec 4.1 and 4.2, exactly as written there. `AuditedAttribute` targets classes (`Inherited = false`); the other two target properties. `EntityChange` and both events are public sealed records in namespaces `TemplateName.SharedKernel.Auditing` and `TemplateName.Application.Common.Auditing`.

- [ ] **Step 1: Write the failing tests** in `EntityChangesRecordedEventTests`:
  - `Domain_event_round_trips_through_System_Text_Json`: serialize with `JsonSerializer.Serialize(event, event.GetType())` and deserialize; assert `Module`, `UserId`, `SessionId`, `TraceId` and every `EntityChange` (including `Action` and the `Changes` text) are equal. This is how the outbox stores it (`DomainEventsToOutboxInterceptor`).
  - `Attributes_have_the_declared_targets`: `AuditedAttribute` allows only `AttributeTargets.Class` and is not inherited; `AuditIgnoreAttribute` and `AuditMaskAttribute` allow only `AttributeTargets.Property`.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~EntityChangesRecordedEventTests"`. Expected: build failure (types missing).
- [ ] **Step 3: Implement the types** per spec 4.1 and 4.2. XML-document each as the neighbouring types are.
- [ ] **Step 4: Run** the same command. Expected: PASS.
- [ ] **Step 5: Commit** `feat(shared-kernel): add audit attributes and change set events`.

## Task 2: Change extraction

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Auditing/EntityChangeExtractor.cs`
- Test: `tests/TemplateName.UnitTests/Infrastructure/Auditing/EntityChangeExtractorTests.cs`, `tests/TemplateName.UnitTests/Infrastructure/Auditing/AuditTestDbContext.cs` (test entities and context)

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `internal static class EntityChangeExtractor` with `internal const int MaxValueLength = 4000;`, `internal const string MaskedValue = "****";` and `internal static IReadOnlyList<EntityChange> Extract(ChangeTracker changeTracker)`. It implements spec 5.1 to 5.3 and nothing else: it does not read `ICurrentUser`, the clock or the outbox. Throws `InvalidOperationException` (message names the entity type) for an `Added` audited entry whose key property has `IsTemporary`.

- [ ] **Step 1: Write the test context.** `AuditTestDbContext : DbContext` uses `UseSqlServer("Server=unused;Database=unused")` (never opened: the tests only track entities and call `ChangeTracker.DetectChanges()`). Test entities: `[Audited] AuditedItem` (Guid key `ValueGeneratedNever`, `IAuditable`, `ISoftDeletable`, `Title`, `Count` int, `Due` `DateOnly`, `State` enum stored as string by a converter, `Flag` enum with no converter, `Note` nullable string, `[AuditIgnore] Ignored`, `[AuditMask] Secret`, `RowVersion` byte[] `IsRowVersion()`); `[Audited] AuditedLink` (composite key `(LeftId, RightId)`); `[Audited] AuditedIdentity` (int identity key); `Unaudited` (no attribute).
- [ ] **Step 2: Write the failing tests**, one fact each, asserting the exact `Changes` JSON (compare parsed JSON, not text order):
  - `Added_entity_is_Created_with_non_null_audited_values_only` (null `Note` absent; `Ignored`, stamps, `RowVersion`, `IsDeleted` absent).
  - `Modified_entity_is_Updated_with_old_and_new_of_changed_audited_properties_only`, including a change to null (`{"old":"x","new":null}`) and one from null.
  - `Deleted_entity_is_Deleted_with_old_values`.
  - `IsDeleted_false_to_true_is_SoftDeleted` and `true_to_false_is_Restored`, each with `{}` when nothing else changed and with the other changed property when one did.
  - `Masked_property_change_is_written_as_asterisks_on_both_sides_and_never_with_its_value`; `Ignored_property_never_appears`.
  - `Converter_values_are_written`: `Due` as `"2026-10-28"`, `State` by its stored name, `Flag` by its enum name.
  - `Long_value_is_cut_to_4000_characters_ending_in_an_ellipsis` (result length 4000).
  - `Composite_key_is_written_as_Name_equals_value_joined_by_semicolons` (`LeftId=…;RightId=…`); single key is the plain invariant string.
  - `Entities_without_the_attribute_are_not_visited` (`Unaudited`).
  - `A_save_that_changes_only_ignored_stamp_or_row_version_columns_returns_no_change` (review focus 1).
  - `Added_entity_with_a_temporary_store_key_throws_naming_the_type` (`AuditedIdentity`; review focus 2).
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~EntityChangeExtractorTests"`. Expected: build failure.
- [ ] **Step 4: Implement `Extract`.** Cache per CLR type: whether it is audited, and its audited property list (scalar, no `[AuditIgnore]`, not `IAuditable`/`ISoftDeletable` columns, not store-generated on add or update such as row versions). Read values with `property.CurrentValue`/`OriginalValue`, apply the property's type-mapping converter when present, an enum without a converter becomes its name. Build the JSON with `Utf8JsonWriter`.
- [ ] **Step 5: Run** the same command. Expected: PASS.
- [ ] **Step 6: Commit** `feat(infrastructure): extract audited entity changes`.

## Task 3: Interceptor, handler and registration

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Auditing/AuditTrailInterceptor.cs`, `EntityChangesRecordedDomainEventHandler.cs`
- Modify: `Outbox/OutboxEventTypes.cs` (constructor adds the record), `Persistence/ModuleDbContextExtensions.cs` (order and comment), `InfrastructureServiceCollectionExtensions.cs` (register the interceptor scoped like `AuditableEntityInterceptor`; `TryAddEnumerable` the handler as `IDomainEventHandler<EntityChangesRecordedDomainEvent>`)
- Modify: `tests/TemplateName.IntegrationTests/Persistence/TestDbContext.cs` (add `[Audited] AuditedTestAggregate`: Guid key `ValueGeneratedNever`, `IAuditable`, `ISoftDeletable`, `Title`, `[AuditIgnore] Counter`; create its table the way `TestAggregate`'s is created)
- Test: `tests/TemplateName.UnitTests/Infrastructure/Auditing/EntityChangesRecordedDomainEventHandlerTests.cs`, `AuditTrailInterceptorTests.cs`, `OutboxEventTypesTests.cs`; `tests/TemplateName.IntegrationTests/Persistence/AuditTrailInterceptorPersistenceTests.cs`

**Interfaces:**
- Consumes: `EntityChangeExtractor.Extract` (Task 2); `ICurrentUser`, `TimeProvider`, `IOutboxMessageContext`, `IIntegrationEventPublisher`.
- Produces: `internal sealed class AuditTrailInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : SaveChangesInterceptor` implementing spec 4.3, 5.4 and 5.5 (pending row remembered per context in a `ConditionalWeakTable<DbContext, OutboxMessage>`, content replaced on the next save, forgotten in `SavedChanges`; `Module` = `context.Model.GetDefaultSchema()`; `TraceId` = `Activity.Current?.TraceId.ToHexString()`; throws `InvalidOperationException` naming the context when it finds audited changes and the context has no `OutboxMessage` mapping). `internal sealed class EntityChangesRecordedDomainEventHandler(IOutboxMessageContext messageContext, IIntegrationEventPublisher publisher) : IDomainEventHandler<EntityChangesRecordedDomainEvent>`.

- [ ] **Step 1: Write failing unit tests.**
  - Handler: publishes one `EntityChangesRecordedIntegrationEvent` whose `Id` and `OccurredAt` come from the message context and whose other fields equal the domain event's (fake publisher captures it).
  - `OutboxEventTypes<TContext>` built from an assembly with no domain events still finds `typeof(EntityChangesRecordedDomainEvent).FullName`.
  - Interceptor, using `AuditTestDbContext` from Task 2 with the interceptor added and no outbox mapping: `SaveChanges()` with an audited change throws `InvalidOperationException` naming the context (the interceptor runs before any connection is opened); with only an ignored change the exception, if any, is not the audit one.
- [ ] **Step 2: Write failing integration tests** in `AuditTrailInterceptorPersistenceTests` (against `TestDbContext`, fake current user via `Factory.CurrentUser`, clock via `Factory.Time`):
  - `Audited_change_writes_one_audit_outbox_row_in_the_same_save`: add an `AuditedTestAggregate`, save, assert exactly one `OutboxMessage` of type `EntityChangesRecordedDomainEvent` whose content has `UserId` = the current user, one `Created` change, and `OccurredAt` = the fake clock.
  - `Save_that_changes_only_an_ignored_property_writes_no_audit_row` (review focus 1).
  - `Save_with_no_signed_in_user_records_null_user_and_session` (review focus 4).
  - `Failed_save_retried_on_the_same_context_records_the_change_once` (review focus 3): cause a `DbUpdateConcurrencyException` by changing the row's `RowVersion` from another context, save (fails), reload to resolve, save again; assert one audit row in the outbox and one change.
  - `Dispatching_the_row_publishes_the_integration_event_with_the_outbox_message_id`: run the `OutboxDispatcher<TestDbContext>` and assert `Factory.IntegrationEvents` holds one `EntityChangesRecordedIntegrationEvent` with `Id` = the outbox message id.
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~Auditing|FullyQualifiedName~AuditTrailInterceptor|FullyQualifiedName~OutboxEventTypes"` in both test projects. Expected: failures (types missing).
- [ ] **Step 4: Implement** the interceptor, handler, registry change, registrations and the interceptor order; update the comment above `AddInterceptors` per spec 4.5.
- [ ] **Step 5: Run** the same filters, then the whole `Outbox` and `Persistence` test folders to see what the new row changed. Expected: PASS (no existing test aggregate is `[Audited]`, so no existing assertion should move).
- [ ] **Step 6: Commit** `feat(infrastructure): record audited changes through the outbox`.

## Task 4: Audit module skeleton, schema and consumer

**Files:**
- Create: `src/Modules/Audit/TemplateName.Modules.Audit/TemplateName.Modules.Audit.csproj` (the Notifications project's references minus `Auth.Contracts`, MailKit, JwtBearer, Scriban and the embedded templates), `AuditModule.cs`, `Domain/Trails/AuditTrail.cs`, `Application/Abstractions/{IAuditTrailRepository,IInbox,IUnitOfWork}.cs`, `Application/AuditPermissions.cs`, `Application/AuditPermissionSource.cs`, `Application/Trails/Record/EntityChangesRecordedIntegrationEventHandler.cs`, `Infrastructure/Persistence/{AuditDbContext,AuditTrailConfiguration,AuditTrailRepository,ModuleInbox}.cs`, `Infrastructure/Persistence/Migrations/` (generated), `docs/modules/audit.md`
- Modify: `TemplateName.slnx`, `src/Host/TemplateName.Api/TemplateName.Api.csproj` and `Program.cs` (`AddAuditModule`, after `AddSampleModule`), the three test projects' references, `tests/TemplateName.ArchitectureTests/Assemblies.cs` (`Modules`)
- Test: `tests/TemplateName.IntegrationTests/Audit/AuditConsumerTests.cs`, `tests/TemplateName.IntegrationTests/Audit/AuditPersistenceTests.cs`

**Interfaces:**
- Consumes: `EntityChangesRecordedIntegrationEvent` (Task 1); `Inbox<TContext>`, `AddInbox<TContext>`, `ApplyInbox`, `AddModuleDbContext<TContext>(AuditDbContext.Schema)`, `AddApplicationHandlers`, `IPermissionSource` (as `NotificationsModule` uses them).
- Produces: `public static class AuditModule { IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration); IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app); }` (the second maps nothing yet; Task 5 fills it). `internal sealed class AuditTrail : Entity<long>` with `ChangeSetId`, `OccurredAt` (`DateTimeOffset`, like `AuthAuditLog`), `UserId`, `SessionId`, `Module`, `EntityType`, `EntityId`, `Action`, `Changes`, `TraceId`, and `static AuditTrail Create(Guid changeSetId, DateTimeOffset occurredAt, Guid? userId, Guid? sessionId, string module, EntityChange change, string? traceId)`. `AuditDbContext.Schema = "audit"`. `AuditPermissions.TrailsView = "audit.trails.view"`.

- [ ] **Step 1: Write the failing integration tests.**
  - `AuditPersistenceTests`: the host migrates the `audit` schema (`AuditTrails` and `InboxMessages` exist; `AuditTrails.Changes` has an `ISJSON` check that rejects `'not json'`); the four indexes of spec 6.1 exist by column list; the permission `audit.trails.view` is seeded and held by `SuperAdmin` and `Admin`; the `audit` health check is in `/health/ready`.
  - `AuditConsumerTests`, calling `IIntegrationEventPublisher.PublishAsync` with a two-change event: two rows with the event id as `ChangeSetId`, the user, session, module, trace id and `OccurredAt`; `Duplicate_delivery_writes_the_rows_once` (publish twice); `A_delivery_that_races_past_the_inbox_check_is_dropped_as_processed` (replace `IInbox` with a decorator whose `HasProcessedAsync` returns false, publish twice, assert one set of rows and no exception).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Audit"`. Expected: build failure.
- [ ] **Step 3: Create the project and wire it in** (slnx, host reference and `Program.cs`, test-project references, `Assemblies.Modules`). `AddAuditModule` registers `AddModuleDbContext<AuditDbContext>`, `AddInbox<AuditDbContext>`, `AddApplicationHandlers(typeof(AuditModule).Assembly)`, the repository, `IInbox`, `IUnitOfWork` and the permission source, as `NotificationsModule` does. `AuditDbContext` calls `ApplyInbox()`, not `ApplyOutbox()`; configure the table per spec 6.1 (the `ISJSON` check via `HasCheckConstraint`, `Action` stored as text with `HasConversion<string>()`, `Id` identity).
- [ ] **Step 4: Implement the consumer** per spec 6.2 (inbox check, one `AuditTrail` per change, record, one save, `Inbox<TContext>.IsDuplicate` handling that clears the change tracker).
- [ ] **Step 5: Generate the migration**: `dotnet ef migrations add CreateAuditSchema --project src/Modules/Audit/TemplateName.Modules.Audit --startup-project src/Host/TemplateName.Api --context AuditDbContext --output-dir Infrastructure/Persistence/Migrations`. Check the generated indexes against spec 6.1.
- [ ] **Step 6: Write `docs/modules/audit.md`** from `docs/modules/_template.md` with every required heading in order (Purpose and boundaries, Endpoints, Domain model, Error codes, Events, Configuration, Data, Background processing, Observability, Testing); write real text for Purpose, Data and Events now and "None." where nothing applies yet. `DocumentationTests` requires the file and the order.
- [ ] **Step 7: Run** the `Audit` filter, then `dotnet test tests/TemplateName.ArchitectureTests --no-build -c Release`. Expected: PASS (the module rules now include Audit).
- [ ] **Step 8: Commit** `feat(audit): add the Audit module schema and consumer`.

## Task 5: Trails endpoint

**Files:**
- Create: `src/Modules/Audit/TemplateName.Modules.Audit/Application/Trails/List/{ListAuditTrailsQuery,AuditTrailResponse,AuditTrailSortFields,ListAuditTrailsQueryValidator,ListAuditTrailsQueryHandler}.cs`, `Endpoints/{AdminAuditTrailEndpoints,AdminAuditTrailRequests}.cs`
- Modify: `AuditModule.cs` (`MapAuditEndpoints` maps the group), `tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json` (accept the received file, ADR 0011), `docs/modules/audit.md` (Endpoints)
- Test: `tests/TemplateName.UnitTests/Audit/ListAuditTrailsQueryValidatorTests.cs`, `tests/TemplateName.IntegrationTests/Audit/AuditTrailEndpointTests.cs`

**Interfaces:**
- Consumes: `CursorPageRequest`, `CursorPage<T>`, `SortField`, `SortSpecification`, `KeysetQuery`, `CursorPageBuilder`, `IDbConnectionFactory`, `RequirePermission`, `AuditPermissions.TrailsView`. Follow `ListAuditLogsQueryHandler`, `AuditLogSortFields` and `AdminAuditLogEndpoints` in the Auth module closely for structure.
- Produces: `internal sealed record ListAuditTrailsQuery(CursorPageRequest Page, string? EntityType, string? EntityId, Guid? UserId, Guid? ChangeSetId, string? Module, string? Action, string? From, string? To) : IQuery<CursorPage<AuditTrailResponse>>`; `internal sealed record AuditTrailResponse(long Id, Guid ChangeSetId, DateTime OccurredAt, Guid? UserId, Guid? SessionId, string Module, string EntityType, string EntityId, string Action, JsonElement Changes, string? TraceId)`; sort field `occurredAt` (`[OccurredAt]`, `typeof(DateTime)`), default `-occurredAt`, tie-breaker `id` (`[Id]`, `typeof(long)`); route group `admin/audit/trails`, tag `Audit administration`, operation name `ListAuditTrails`.

- [ ] **Step 1: Write failing unit tests** for the validator: page size 1 to 100; `action` must be one of the `EntityChangeAction` names (exact, case-sensitive) when not blank; `from` and `to` parse as the Auth list's timestamps do (ISO 8601, UTC without offset); `to` earlier than `from` fails with the `'To' must not be earlier than 'From'.` wording (review focus 5).
- [ ] **Step 2: Write failing integration tests** in `AuditTrailEndpointTests`, seeding rows through `AuditDbContext`:
  - 401 without a token; 403 without `audit.trails.view`; 200 with it.
  - Each filter alone, and `entityType`+`entityId`+`userId` together; a combination that matches nothing returns an empty page with `nextCursor` null (review focus 5).
  - `changes` is a JSON object in the response, not a string.
  - Default order newest first; `sort=occurredAt` ascending; two rows with equal `OccurredAt` keep the `Id` order; paging forward then backward returns the same pages; `includeTotalCount=true` returns the count; a cursor from one filter replayed with another answers `400 pagination.cursor_mismatch`.
  - `from` after `to` answers `400 validation.failed` with `errors.to`.
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~AuditTrail"`. Expected: FAIL (route missing).
- [ ] **Step 4: Implement** the query, validator, handler (Dapper; every filter a parameter; `1 = 1` base clause as in the Notifications list) and endpoint. Deserialize `Changes` into a `JsonElement` in the mapping. `entityId`, `module` and `entityType` are exact matches.
- [ ] **Step 5: Run** the same filter, then `OpenApiSnapshotTests`. Expected: the snapshot test fails showing only the new route; replace the `.verified.json` with the `.received.json` and re-run. Expected: PASS.
- [ ] **Step 6: Check the execution plan** of the unfiltered and `entityType`+`entityId` queries with `SqlExecutionPlan` (tests/Infrastructure): each uses its index of spec 6.1 (seek or ordered scan, no sort operator).
- [ ] **Step 7: Update `docs/modules/audit.md`** (Endpoints table, filters, response).
- [ ] **Step 8: Commit** `feat(audit): add the admin audit trails endpoint`.

## Task 6: Retention

**Files:**
- Create: `src/Modules/Audit/TemplateName.Modules.Audit/Infrastructure/Retention/{AuditRetentionOptions,AuditTrailsCleanupJob}.cs`
- Modify: `AuditModule.cs` (`AddRetention`), `docs/modules/audit.md` (Configuration, Background processing). The host's `appsettings.json` gets no `Audit` key: the default is `null`, which JSON cannot express with a comment, so the module page documents it.
- Test: `tests/TemplateName.UnitTests/Audit/AuditRetentionOptionsTests.cs`, `tests/TemplateName.IntegrationTests/Audit/AuditTrailsCleanupJobTests.cs`

**Interfaces:**
- Consumes: `BatchedCleanupJob`, `AddRecurringJob<T>`, `RetentionOptions`, `JobsOptions`; model the options on `AuthRetentionOptions` (`SectionName = "Audit:Retention"`, `Trails` as `TimeSpan?`, negative refused) and the job on `AuditLogsCleanupJob` (name constant `AuditTrailsCleanup`, daily, `DELETE TOP (@Batch) FROM {table} WHERE OccurredAt < @Cutoff;`).

- [ ] **Step 1: Write failing tests.** Unit: a negative `Trails` fails validation; zero and `null` pass. Integration, copying the shape of the Auth `AuditLogsCleanupJob` tests: the job is registered daily under its name; rows either side of the cutoff (strictly older goes, equal stays); a backlog larger than one batch drains across runs; `null` and zero delete nothing; the execution plan of `DeleteStatement` uses the `(OccurredAt, Id)` index.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AuditRetention|FullyQualifiedName~AuditTrailsCleanup"`. Expected: build failure.
- [ ] **Step 3: Implement** the options (validated on start through `ValidateDataAnnotations` and `IValidatableObject`) and the job; register both in `AddRetention`.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Update `docs/modules/audit.md`** and commit `feat(audit): add the optional audit trail retention`.

## Task 7: First audited entities and architecture rules

**Files:**
- Modify (attributes only, per spec 6.5): `src/Modules/Sample/.../Domain/LeaveRequests/LeaveRequest.cs`, `src/Modules/Auth/.../Domain/Users/User.cs`, `Domain/Users/UserRole.cs`, `Domain/Roles/Role.cs`, `Domain/Roles/RolePermission.cs`; their `*Configuration.cs` only where a key is not `ValueGeneratedNever()` (the composite keys of `RolePermission` and `UserRole` likewise)
- Create: `tests/TemplateName.ArchitectureTests/AuditingTests.cs`, `tests/TemplateName.IntegrationTests/Audit/AuditTrailEndToEndTests.cs`, `tests/TemplateName.IntegrationTests/Infrastructure/AuditOutboxFilter.cs`
- Modify: existing Auth, Sample and Outbox integration tests that read every `OutboxMessage` and now see audit rows (the list comes from running the suite)

**Interfaces:**
- Produces: `internal static class AuditOutboxFilter { public static IQueryable<OutboxMessage> WithoutAuditRows(this IQueryable<OutboxMessage> messages); }` (excludes `Type == typeof(EntityChangesRecordedDomainEvent).FullName`); existing tests use it where they assert counts, take a single row, or list rows by type.

- [ ] **Step 1: Write the failing architecture tests** in `AuditingTests` (reflect over `Assemblies.Modules` types with `[Audited]`, and over the EF model of each module context where the key mapping is needed, as `ConventionTests` does for its model rules):
  - every `[Audited]` entity's key properties have `ValueGenerated == Never`;
  - no audited entity has an owned or complex property;
  - the secret-name rule: a property of an `[Audited]` entity (not `[AuditIgnore]`, not `[AuditMask]`) whose name ends in `Hash`, `Secret`, `Token`, `Salt`, `Key` or `Stamp`, or equals `Password`, fails; `PasswordChangedAt` passes; `User.PasswordHash` and `User.SecurityStamp` carry `[AuditIgnore]`;
  - the three attributes and the `EntityChange*` types are declared in `TemplateName.SharedKernel`;
  - `TemplateName.Modules.Audit` references no other module and no module references it (the existing boundary rules already include it; assert the Audit-specific facts explicitly);
  - the five entities of spec 6.5 are the audited set today.
- [ ] **Step 2: Write the failing end-to-end tests** in `AuditTrailEndToEndTests` (run the Auth and Sample outbox dispatchers by hand: `Outbox:Enabled` is false in tests):
  - Submit then approve a leave request through the API: two change sets, `Created` then `Updated` with `Status` `Pending` to `Approved` and `ApproverId`, each with the caller's `UserId`, `SessionId` and a non-null `TraceId`; the rows are readable through `GET /admin/audit/trails?entityType=LeaveRequest&entityId={id}`.
  - Soft-delete a leave request through its context: a `SoftDeleted` row.
  - Change a role's permissions through the Auth admin endpoint: `RolePermission` `Created` and `Deleted` rows with ids `RoleId=…;PermissionId=…`.
  - Suspend a user through the admin endpoint: an `Updated` row with `Status` `Active` to `Suspended` and no `PasswordHash`, `SecurityStamp` or `RowVersion` key in any `Changes`.
  - A user's sign-in (which saves `LastLoginAt` only) writes no audit row (review focus 1, end to end).
  - The seeder's first run writes trail rows with null `UserId` and `SessionId` (review focus 4).
  - A concurrency conflict on a leave request (two stale approvals) leaves exactly one `Updated` trail.
  - Every context with an audited entity maps the outbox (`AuthDbContext`, `SampleDbContext`).
- [ ] **Step 3: Run** `dotnet test tests/TemplateName.ArchitectureTests --filter "FullyQualifiedName~AuditingTests"` and the `AuditTrailEndToEnd` filter. Expected: FAIL (no entity is audited).
- [ ] **Step 4: Add the attributes** to the five entities exactly as spec 6.5 lists them (`[Audited]` on the class; `[AuditIgnore]` on `NormalizedEmail`, `LastLoginAt`, `AccessFailedCount`, `PasswordHash` and `SecurityStamp` of `User` and on `NormalizedName` of `Role`). Where a configuration lacks `ValueGeneratedNever()` on a key, add it, then run `dotnet ef migrations has-pending-model-changes` for each context: it must report none. If a context reports a change, stop and ask rather than adding a migration.
- [ ] **Step 5: Run the full suite** (`dotnet build -c Release`, then `dotnet test --no-build -c Release`). Existing tests that count, take `Single` of, or list all `OutboxMessage` rows of `AuthDbContext` or `SampleDbContext` will fail; apply `WithoutAuditRows()` to exactly those queries, with no other change to what the tests mean. Tests that scan outbox text for secrets stay unfiltered: they now also prove no secret reaches an audit row.
- [ ] **Step 6: Re-run the full suite.** Expected: all green, including `OpenApiSnapshotTests`.
- [ ] **Step 7: Commit** `feat(audit): audit leave requests, users, roles and their grants`.

## Task 8: Documents, decision record and template

**Files:**
- Create: `docs/adr/0030-audit-trail-through-the-owning-modules-outbox.md`
- Modify: `docs/modules/audit.md` (complete every section, the limits of spec 2, the operations notes of 6.4 and 7), `docs/modules/auth.md` and `sample.md` (audited entities, two logs, the retention text of `AuditLogs`), `docs/architecture/overview.md` (module list, `audit` schema row, interceptor order, arrows), `docs/building-blocks/shared-kernel.md`, `application-common.md`, `infrastructure-common.md`, `docs/adr/0028-retention-policy-and-the-inbox-window.md` (follow-up closed), `docs/README.md` (modules and ADR index), `docs/blueprint-review.md` (row 5b done, review 2.3 points at ADR 0030), `docs/coding-conventions.md` (audit a new entity with `[Audited]`; secrets with `[AuditIgnore]` or `[AuditMask]`), `build/template-content/CLAUDE.md` and `build/template-content/README.md`, `README.md`
- Test: `tests/TemplateName.ArchitectureTests/DocumentationTests.cs` (existing, unchanged), `build/scripts/template-smoke.sh`

- [ ] **Step 1: Write ADR 0030** in the format of ADR 0028 and 0029 (Status, Date, Deciders, Context, Options considered, Decision, Consequences): the options and rejections of D2, D3 and D5 of the spec, the building blocks, the key and secret rules, and the limits (no raw SQL, no hash chain).
- [ ] **Step 2: Update the documents** listed above, each change limited to what this plan changed. Wherever a doc says an archive "will be decided in Plan 5b" or that the audit trail "moved to Plan 5b", say it is done and where.
- [ ] **Step 3: Run** `dotnet test tests/TemplateName.ArchitectureTests --no-build -c Release` and `dotnet format --verify-no-changes`. Expected: PASS.
- [ ] **Step 4: Run** `bash build/scripts/template-smoke.sh`. Expected: the generated project builds with the Audit module renamed with the others and its tests pass. If it fails because the template tooling lists modules by name, add `Audit` where `Notifications` is listed.
- [ ] **Step 5: Run the full suite once more** after `dotnet build -c Release` (warnings are errors). Expected: green.
- [ ] **Step 6: Commit** `docs(audit): document the Audit module and add ADR 0030`.

## Self-review

- **Spec coverage:** 4.1 and 4.2 Task 1; 4.3 to 4.5 and 5.1 to 5.5 Tasks 2 and 3; 6.1 and 6.2 Task 4; 6.3 Task 5; 6.4 Task 6; 6.5 and the architecture and end-to-end tests of section 8 Task 7; section 9 and the smoke script Task 8. Section 7 failure rows: temporary key (Task 2), missing outbox mapping (Task 3), duplicate delivery (Task 4); "module not registered" and "consumer database down" are properties of existing machinery and are documented, not tested, in Task 8.
- **Types:** `EntityChange`, `EntityChangeAction`, `EntityChangesRecordedDomainEvent` (SharedKernel), `EntityChangesRecordedIntegrationEvent` (Application.Common), `EntityChangeExtractor.Extract`, `AuditTrailInterceptor`, `AuditTrail.Create`, `AuditDbContext.Schema`, `AuditPermissions.TrailsView` and `AuditOutboxFilter.WithoutAuditRows` are each defined in one task and used by the same name in later ones.
- **Known risk, stated:** Task 7 Step 5 changes existing tests by design, because audit rows now appear in the `OutboxMessages` of Auth and Sample. The filter is the only change allowed there.
