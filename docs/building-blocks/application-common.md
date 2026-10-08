# Application.Common (`TemplateName.Application.Common`)

## Purpose

The application-layer contracts modules code against: command, query and domain event handlers with their registration and decorators (ADR 0005), the current user, the read-side connection factory, error-message resources (ADR 0009) and cursor pagination (ADR 0010). It depends on SharedKernel, FluentValidation, Scrutor, Dapper (for `KeysetQuery.Parameters`) and the `Microsoft.Extensions` abstractions, never on Infrastructure.Common or Web.Common (architecture test).

Code: `src/BuildingBlocks/TemplateName.Application.Common/`.

## Public types and extension methods

### Messaging

| Type | What it is |
|---|---|
| `ICommand`, `ICommand<TResponse>`, `IBaseCommand` | Markers for commands without and with a success value. |
| `IQuery<TResponse>` | Marker for a query. |
| `ICommandHandler<TCommand>` | `Task<Result> HandleAsync(TCommand, CancellationToken)`. |
| `ICommandHandler<TCommand, TResponse>` | `Task<Result<TResponse>> HandleAsync(TCommand, CancellationToken)`. |
| `IQueryHandler<TQuery, TResponse>` | `Task<Result<TResponse>> HandleAsync(TQuery, CancellationToken)`. |
| `IDomainEventHandler<TEvent>` | `Task HandleAsync(TEvent, CancellationToken)`; run by the module outbox after the save, at least once (ADR 0007). Must be idempotent. |

| Extension | Does |
|---|---|
| `services.AddApplicationHandlers(assembly)` | Registers every handler (including `internal` ones) of the four interfaces above, and every FluentValidation validator in the assembly, as scoped. |
| `services.AddApplicationDecorators()` | Wraps every registered command and query handler with the validation decorator, then the logging decorator (outermost). Call it once, last, after every module. Domain event handlers are not decorated. |

- **Validation decorator:** runs every `IValidator<T>` of the message; on failure it returns a `ValidationError` (`validation.failed`) without calling the handler. Field keys are camel-cased paths (`Items[0].Name` → `items[0].name`).
- **Logging decorator:** logs `Processing {RequestName}` and `Completed {RequestName} in {ElapsedMs} ms` (Information) or `Failed {RequestName} with {ErrorCode} in {ElapsedMs} ms` (Warning). Exceptions are not caught; the exception handler logs them once.

### Identity and data

| Type | What it is |
|---|---|
| `ICurrentUser` | `Guid? UserId`, `bool IsAuthenticated`. Web.Common implements it from the `sub` (or name identifier) claim. |
| `PermissionDefinition` | `sealed record (Code, Module, Name, Description)`: one permission a module declares. `Code` is `module.resource.action` in snake case (`auth.user.view`); `Module` is its first segment; `Name` and `Description` are English text for administration screens. |
| `IPermissionSource` | `IReadOnlyCollection<PermissionDefinition> Permissions`. Any module registers one as a singleton; the Auth module collects every registered source and syncs the definitions into its permission table at startup. |
| `IPermissionChecker` | `Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken)`: whether the user holds the permission through any role. The Auth module implements it (cached per user, ADR 0016); other modules depend on the interface only. |
| `IDbConnectionFactory` | `Task<DbConnection> OpenConnectionAsync(CancellationToken)`: an open connection for Dapper reads. Infrastructure.Common implements it. |

### Localization

| Type / extension | What it is |
|---|---|
| `services.AddErrorMessages<TResource>()` | Registers the `*ErrorMessages` resource set whose marker class is `TResource` (the `.resx` files sit beside it) and `IErrorMessageLocalizer`. Sets are searched in registration order; registering one twice has no effect. |
| `IErrorMessageLocalizer` | `Localize(code, parameters, fallback)`: the message for `code` in `CurrentUICulture` from the first set that has it, with `{name}` placeholders filled from `parameters` (unknown placeholders stay as they are), else `fallback`. |

### Pagination

Cursor (keyset) pagination for list queries (ADR 0010); the Sample module's list is the worked example, and the README's "Adding a paged list endpoint" recipe walks through it.

| Type | What it is |
|---|---|
| `CursorPageRequest(PageSize = 20, Cursor, Sort, IncludeTotalCount = false)` | The paging part of a list query; `DefaultPageSize` 20, `MaxPageSize` 100. |
| `CursorPage<T>(Items, PageSize, NextCursor, PreviousCursor, TotalCount)` | The response; `totalCount` is omitted from JSON when null. The generated OpenAPI schema still lists `totalCount` as required (a generator quirk pinned by the OpenAPI snapshot, like the missing `Idempotency-Key` header on idempotent endpoints, see [infrastructure-common](infrastructure-common.md#idempotency)); clients must treat it as optional. |
| `SortField(Name, Column, ValueType)` | One allow-listed sort field: camelCase API name, bracketed column constant, CLR type. |
| `SortSpecification.Parse(sort, allowedFields, idField, defaultSort)` | Parses `-createdAt,startDate`; appends the id tie-breaker in the direction of the last term; an unknown, empty or repeated field fails with `pagination.invalid_sort`. `Signature` is the canonical text cursors carry. |
| `Cursor`, `CursorDirection` | A decoded position: direction, sort signature, filter hash, key values. |
| `CursorCodec` | `Encode`, `Decode(token, sort, filterHash)` (base64url JSON, at most `MaxTokenLength` 1 024 characters; not signed, because filters are applied server-side and key values are SQL parameters), `ComputeFilterHash(canonicalFilter)`. |
| `KeysetSqlBuilder.Build(sort, cursor, pageSize)` → `KeysetQuery` | The `WHERE` and `ORDER BY` fragments and Dapper parameters for one page (fetches one extra row to detect a next page). |
| `CursorPageBuilder.Build(…)` | Trims the extra row, restores order for backward pages, and encodes `nextCursor` / `previousCursor` from the fetched rows' key values. |
| `PaginationErrors` | `InvalidCursor` (`pagination.invalid_cursor`), `CursorMismatch` (`pagination.cursor_mismatch`), `InvalidSort(allowed)` (`pagination.invalid_sort`, `params.allowed`). Messages are in `CommonErrorMessages` (Web.Common). |

## Configuration

None. (Pagination limits are constants on `CursorPageRequest`.)

## How to use from a module

```csharp
// Application/LeaveRequests/Approve/ApproveLeaveRequestCommand.cs
internal sealed record ApproveLeaveRequestCommand(Guid LeaveRequestId, Guid ApproverId) : ICommand;

// Application/LeaveRequests/Approve/ApproveLeaveRequestCommandHandler.cs
internal sealed class ApproveLeaveRequestCommandHandler(ILeaveRequestRepository repository, IUnitOfWork unitOfWork)
    : ICommandHandler<ApproveLeaveRequestCommand>
{
    public async Task<Result> HandleAsync(ApproveLeaveRequestCommand command, CancellationToken cancellationToken)
    {
        var leaveRequest = await repository.GetByIdAsync(command.LeaveRequestId, cancellationToken);
        if (leaveRequest is null)
        {
            return Result.Failure(LeaveRequestErrors.NotFound(command.LeaveRequestId));
        }

        var result = leaveRequest.Approve(command.ApproverId);
        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// {Module}Module.cs
services.AddApplicationHandlers(typeof(SampleModule).Assembly);
services.AddErrorMessages<SampleErrorMessages>();
```

Endpoints inject the handler interface (`ICommandHandler<ApproveLeaveRequestCommand> handler`) and get the decorated instance, because the host calls `AddApplicationDecorators()` last.

## Tests

`tests/TemplateName.UnitTests/Application/` (handler registration, both decorators for every handler shape), `Localization/ErrorMessageLocalizerTests`, and `Pagination/` (`CursorCodecTests`, `SortSpecificationTests`, `KeysetSqlBuilderTests`, `CursorPageBuilderTests`).
