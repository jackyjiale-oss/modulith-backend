# 0002. Result pattern for expected failures

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Handlers regularly fail in expected ways: validation errors, a missing aggregate, a state conflict, a forbidden action. Throwing exceptions for these is slow, hides the failure paths from the method signature, and makes the mapping to HTTP status codes implicit. The template needs one way to return expected failures that the web layer can turn into RFC 9457 ProblemDetails with a stable `code`.

## Options considered
1. Exceptions for all failures — familiar and short; but failure paths are invisible in signatures, exceptions are costly as control flow, and the HTTP mapping lives in a catch-all handler.
2. A `Result` / `Result<T>` return type carrying an `Error` with a code, message and `ErrorType` — failures are explicit in the signature and map directly to status codes; slightly more ceremony in handlers.
3. A third-party result library — less code to write; but adds a dependency and a licence to check for a type that is about 100 lines.

## Decision
We chose option 2, in `TemplateName.SharedKernel`. Expected failures return `Result` or `Result<T>` with an `Error(Code, Message, Type)`, where `ErrorType` is `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden` or `Failure`. Exceptions stay for unexpected failures (bugs, infrastructure faults) and become a 500. `Error` is an unsealed record so that `ValidationError` can extend it with the field-error dictionary (blueprint review, 2.13). A `Result` cannot be a success with an error or a failure without one, and `Result<T>.Value` throws on a failure.

## Consequences
- Positive: failure paths are visible in signatures; one mapping from `ErrorType` to HTTP status; no exceptions as control flow.
- Negative / trade-offs accepted: handlers must check `IsFailure`; reading `Value` of a failed result is a programming error that throws.
- Follow-up actions: map `ErrorType` to ProblemDetails in the web layer; every error code gets a localized message in all supported languages.
