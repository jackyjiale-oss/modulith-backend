# 0005. Injected handlers with Scrutor decorators instead of MediatR

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Application use cases need a uniform shape (one command or query, one handler) and a place for cross-cutting behavior such as validation and logging. The common answer is MediatR with pipeline behaviors, but MediatR moved to a commercial licence from v13 (blueprint review, Section 3), and it dispatches through reflection and a mediator object, which hides who calls whom. The blueprint also listed five decorators (Logging, Validation, Authorization, Transaction, Caching). The review found that Authorization is already enforced at the endpoint, that a generic Transaction decorator cannot know which module's unit of work to commit (Section 2.5), and that caching is clearer written out in the query handler (Section 4).

## Options considered
1. MediatR with pipeline behaviors — familiar and widely documented; but commercial licence from v13 (forbidden in this template), dispatch hidden behind the mediator, and handlers cannot be called without it.
2. Handlers injected directly (`ICommandHandler<TCommand, TResponse>`, `IQueryHandler<TQuery, TResponse>`) and wrapped with Scrutor decorators — plain constructor injection, no mediator, compile-time handler signatures, decorators applied in one place; needs one small dependency (Scrutor, MIT) and a convention for registration order.
3. Hand-written decorators registered one by one in each module — no dependency; but every handler needs its own registration and wrapping code, which is easy to forget.

## Decision
We chose option 2. Contracts live in `TemplateName.Application.Common.Messaging`: `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>`, their handlers (`HandleAsync` returning `Result` or `Result<T>`) and `IDomainEventHandler<TEvent>`. Each module calls `AddApplicationHandlers(assembly)`, which scans for handlers (internal ones included) and FluentValidation validators and registers them as scoped. The host calls `AddApplicationDecorators()` once, last, after every module has registered its handlers. It uses Scrutor's `TryDecorate` to register Validation first and then Logging, so Logging is outermost and also records validation failures.

Only two decorators ship:
- `ValidationDecorator` runs all validators and returns a `ValidationError` (keys in camelCase) without calling the inner handler.
- `LoggingDecorator` logs the request, its duration, and failures at Warning with the error code. It doesn't catch exceptions; the global exception handler logs those once.

Authorization stays at the endpoint, transactions stay explicit (handlers call their module's `IUnitOfWork.SaveChangesAsync`, which is atomic and also writes outbox events in the same save), and caching is an explicit `HybridCache.GetOrCreateAsync` in the query handler.

## Consequences
- Positive: no MediatR licence risk; the handler a call goes to is visible in the constructor; decorators are plain classes that are easy to unit test; a new cross-cutting concern is one more `TryDecorate` line.
- Negative / trade-offs accepted: decorator order is a registration convention, covered by a test rather than by the type system; `AddApplicationDecorators()` must be called exactly once and last, because decorating before all handlers are registered would leave some unwrapped; Scrutor is a new dependency (MIT, supports .NET 10).
- Follow-up actions: an architecture test for the `{MessageName}Handler` naming and `internal` handlers; the endpoints of each module resolve handlers directly instead of sending to a mediator.
