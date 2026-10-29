# SharedKernel (`TemplateName.SharedKernel`)

## Purpose

The base types every module's domain is written in: entities and aggregate roots, domain events, the `Result`/`Error` pattern for expected failures (ADR 0002), the audit and soft-delete contracts, and SQL Server-ordered GUIDs (ADR 0004). It has **no package or project dependencies**, which an architecture test enforces, so a module's `Domain` folder depends on SharedKernel only.

Code: `src/BuildingBlocks/TemplateName.SharedKernel/`.

## Public types

| Type | What it is |
|---|---|
| `Entity<TId>` | Base type with an `Id` (protected setter). |
| `AggregateRoot<TId>` | An `Entity<TId>` that records domain events: `protected Raise(IDomainEvent)`, `DomainEvents`, `ClearDomainEvents()`. Implements `IHasDomainEvents`. |
| `IDomainEvent` | Marker for a domain event; implementations are records named `{Noun}{PastTenseVerb}DomainEvent`. |
| `IHasDomainEvents` | What the outbox interceptor reads: `DomainEvents` and `ClearDomainEvents()`. |
| `IIntegrationEvent` | `Guid Id`, `DateTimeOffset OccurredAt`: a fact one module publishes for others (ADR 0018). Implementations are `public sealed record {Noun}{PastTenseVerb}IntegrationEvent` in the publishing module's `.Contracts` project, never stored, so without a version (a breaking change is a new record). `Id` is the outbox message id of the domain event it came from, so a retry keeps it; one domain event maps to at most one integration event. |
| `Result`, `Result<T>` | The outcome of an operation that can fail in an expected way: `IsSuccess`, `IsFailure`, `Error`, and `Value` (throws on a failure). `Result.Success()`, `Result.Failure(error)`, `Result.Success<T>(value)`, `Result.Failure<T>(error)`; a `T` or an `Error` converts implicitly to `Result<T>`. |
| `Error` | An unsealed record `(Code, Message, Type)` with `Parameters`. Factories `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden`, `Failure`; `Error.None` for success. `Code` is `module.snake_case`; `Message` is the English default that the HTTP boundary replaces with the localized text (ADR 0009). |
| `ValidationError` | `sealed record ValidationError(Errors) : Error("validation.failed", …, Validation)`: field messages keyed by property name. |
| `ErrorType` | `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden`, `Failure`; Web.Common maps them to 400, 404, 409, 401, 403 and 500. |
| `IAuditable` | `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy` (UTC), stamped by an EF interceptor. |
| `ISoftDeletable` | `IsDeleted`, `DeletedAt`, `DeletedBy`, set by an EF interceptor instead of deleting the row. |
| `SequentialGuid` | `Create(DateTimeOffset timestamp)`: a GUID whose bytes 10 to 15 hold the Unix milliseconds, which SQL Server sorts first, so clustered-index inserts stay sequential. `Guid.CreateVersion7()` does not do this on SQL Server. |

`Error.Parameters` go to clients as `params`, so they never hold secrets or personal data. Records compare `Parameters` by reference: compare errors that carry parameters by `Code`.

### Auditing

`TemplateName.SharedKernel.Auditing` holds what a module's `Domain` needs to take part in the audit trail ([ADR 0030](../adr/0030-audit-trail-through-the-owning-modules-outbox.md), [Audit module](../modules/audit.md)); a `Domain` folder may depend on nothing else, so the types live here.

| Type | What it is |
|---|---|
| `[Audited]` | Class attribute (`AttributeTargets.Class`, not inherited): the entity's changes are written to the audit trail. Opt-in, so a forgotten attribute loses a trail where it was wanted, never a secret into one. |
| `[AuditIgnore]` | Property attribute: the property is never written to the trail. Use it for secrets, derived columns and high-frequency noise. |
| `[AuditMask]` | Property attribute: a change is recorded, the old and new values are replaced by `"****"`. Use it for a secret whose change is worth proving. |
| `EntityChangeAction` | `Created`, `Updated`, `Deleted`, `SoftDeleted`, `Restored`; stored as its name. |
| `EntityChange` | `sealed record (string EntityType, string EntityId, EntityChangeAction Action, string Changes)`: one changed entity. `EntityType` is the CLR type name without namespace, `EntityId` the key as text (invariant culture; a composite key is `Name=value;Name=value`), `Changes` JSON object text keyed by property name (`{"Status":{"old":"Pending","new":"Approved"}}`). |
| `EntityChangesRecordedDomainEvent` | `sealed record (Guid? UserId, Guid? SessionId, string? TraceId, string Module, IReadOnlyList<EntityChange> Changes) : IDomainEvent`: the change set of one save, the payload of the audit outbox row. No aggregate raises it; `AuditTrailInterceptor` writes it. Its full name is stored in outbox rows, so it is kept stable like a published contract: renaming it would orphan pending rows. |

Rules for an audited entity: its key is `ValueGeneratedNever()` (an application-generated key such as `SequentialGuid.Create`; an architecture test enforces it), it has no owned or complex property, a secret-looking property (a name ending in `Hash`, `Secret`, `Token`, `Salt`, `Key` or `Stamp`, or `Password`) carries `[AuditIgnore]` or `[AuditMask]` (also tested), and a store-generated column, including one with `HasDefaultValue` or `HasDefaultValueSql`, is skipped by rule and so not audited. [Audit: What is recorded](../modules/audit.md#what-is-recorded) has the full rules.

## Configuration

None.

## How to use from a module

```csharp
// Domain/LeaveRequests/LeaveRequest.cs
internal sealed class LeaveRequest : AggregateRoot<Guid>, IAuditable, ISoftDeletable
{
    public Result Approve(Guid approverId)
    {
        if (Status != LeaveRequestStatus.Pending)
        {
            return Result.Failure(LeaveRequestErrors.NotPending);
        }

        Status = LeaveRequestStatus.Approved;
        Raise(new LeaveRequestApprovedDomainEvent(Id, approverId));
        return Result.Success();
    }
}

// Domain/LeaveRequests/LeaveRequestErrors.cs
internal static class LeaveRequestErrors
{
    public static readonly Error NotPending = Error.Conflict("leave.not_pending", "Only a pending leave request can be approved.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("leave.not_found", $"Leave request '{id}' was not found.")
        with { Parameters = new Dictionary<string, object?> { ["id"] = id } };
}
```

New ids come from `SequentialGuid.Create(timeProvider.GetUtcNow())`. Every error code also needs an entry in the module's `Resources/{Module}ErrorMessages.resx`, `.ms.resx` and `.zh-Hans.resx`; `TranslationTests` fail otherwise.

## Tests

`tests/TemplateName.UnitTests/SharedKernel/`: `ResultTests`, `AggregateRootTests`, `SequentialGuidTests` (including SQL Server sort order), `Auditing/EntityChangesRecordedEventTests` (the domain event round-trips through System.Text.Json; the attributes' targets and inheritance).
