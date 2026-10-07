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
| `Result`, `Result<T>` | The outcome of an operation that can fail in an expected way: `IsSuccess`, `IsFailure`, `Error`, and `Value` (throws on a failure). `Result.Success()`, `Result.Failure(error)`, `Result.Success<T>(value)`, `Result.Failure<T>(error)`; a `T` or an `Error` converts implicitly to `Result<T>`. |
| `Error` | An unsealed record `(Code, Message, Type)` with `Parameters`. Factories `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden`, `Failure`; `Error.None` for success. `Code` is `module.snake_case`; `Message` is the English default that the HTTP boundary replaces with the localized text (ADR 0009). |
| `ValidationError` | `sealed record ValidationError(Errors) : Error("validation.failed", …, Validation)`: field messages keyed by property name. |
| `ErrorType` | `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden`, `Failure`; Web.Common maps them to 400, 404, 409, 401, 403 and 500. |
| `IAuditable` | `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy` (UTC), stamped by an EF interceptor. |
| `ISoftDeletable` | `IsDeleted`, `DeletedAt`, `DeletedBy`, set by an EF interceptor instead of deleting the row. |
| `SequentialGuid` | `Create(DateTimeOffset timestamp)`: a GUID whose bytes 10 to 15 hold the Unix milliseconds, which SQL Server sorts first, so clustered-index inserts stay sequential. `Guid.CreateVersion7()` does not do this on SQL Server. |

`Error.Parameters` go to clients as `params`, so they never hold secrets or personal data. Records compare `Parameters` by reference: compare errors that carry parameters by `Code`.

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

`tests/TemplateName.UnitTests/SharedKernel/`: `ResultTests`, `AggregateRootTests`, `SequentialGuidTests` (including SQL Server sort order).
