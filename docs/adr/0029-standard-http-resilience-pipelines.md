# 0029. Standard HTTP resilience pipelines from Microsoft.Extensions.Http.Resilience

- Status: Accepted
- Date: 2026-10-20
- Deciders: Lee Jia Le

## Context
The template makes one outbound HTTP call today, the Auth module's breached-password lookup (`hibp`), and will make more: SMS and email providers, identity providers, a payment gateway. An outbound call fails in ways nothing in our own code does: the server hangs, answers 503 for a second, or is down for minutes while every request of ours piles up behind it. The `hibp` client has only a 2 s timeout and a checker that fails open; that is right for one call and would be copied wrongly, or not at all, for the next.

Plan 5 needs one place that says how an outbound call retries, how long it may take and when it stops calling a service that is down. Reliable asynchronous work already has its own engines (the outbox, the notification delivery worker with its dead-letter schedule), and MailKit SMTP is not HTTP; this decision is about HTTP only.

## Options considered
1. **Nothing but `HttpClient.Timeout`**, a timeout and a `try`/`catch` in each caller (what `hibp` has). No dependency, but every caller writes its own retry loop and its own idea of failure, nothing stops a dead service being called on every request, and none of it is tested together.
2. **Polly written by hand**, a pipeline built in each module from `Polly.Core`. Full control, and the same decisions repeated and drifting in every module, with no common configuration or validation.
3. **`AddStandardResilienceHandler` on each client**, from `Microsoft.Extensions.Http.Resilience` (the package Microsoft ships on top of Polly). It is the right pipeline, but it is configured per call site in code and has no common names or configuration keys.
4. **Named pipelines over the same package, bound from configuration**: one `AddExternalHttpClient(name, pipeline)` that adds the standard strategies (total timeout, retry with exponential backoff and jitter, circuit breaker, attempt timeout) with values from `Resilience:Pipelines:{pipeline}`, validated on start.

## Decision
We chose option 4 (decision D7 of the Plan 5 design).

- **The package** is `Microsoft.Extensions.Http.Resilience` (MIT, no usage fees; it brings Polly), under Central Package Management and the licence check like every package.
- **`AddExternalHttpClient(name, pipeline = ResiliencePipelines.Default)`** in `Infrastructure.Common/Http` is `AddHttpClient` plus `AddResilienceHandler`. Strategies, outermost first: total timeout, retry (exponential backoff with jitter; none when `Retries` is 0; not for unsafe methods unless `RetryUnsafeMethods`), circuit breaker, attempt timeout. The retry and the breaker use the package's standard predicate: 5xx, 408, 429, network errors and the attempt timeout are failures; any other 4xx is a final answer, never retried and never counted. The client has no `HttpClient.Timeout`; the total timeout is its limit.
- **Three pipelines now:** `external-default` (attempt 10 s, total 30 s, 3 retries), `external-critical` (5 retries, total 90 s so their delays fit) and `hibp` (attempt 1 s, one retry, total 2 s). The breaker is the same for all (decision P7: failure ratio 0.5 over 30 s, at least 10 attempts, 30 s open), because the design names a breaker without numbers and these are the package's own defaults. Every value is overridable under `Resilience:Pipelines:{pipeline}` and validated on start: the attempt timeout not above the total, retries 0 to 10, positive timeouts, a ratio above 0 and at most 1 (and the breaker minimums Polly itself enforces).
- **Pipelines for SMS and email providers are not created.** Nothing calls one, so a pipeline would be code without a test that proves it; `infrastructure-common.md` shows how to add one, with its own name and settings, in two lines.
- **Time is `TimeProvider`.** The strategies use the registered provider, so every behaviour (the attempt timeout, the total, the retry delays, the breaker opening and half-opening) is tested on a `FakeTimeProvider` over a scripted handler, with no real waiting.
- **`hibp` keeps its own 2 s budget.** The checker's linked token (request, headers and body) stays the outer limit and its fail-open behaviour stays; the pipeline is inside it with the same 2 s total. The checker also treats Polly's `ExecutionRejectedException` (an open breaker, a pipeline timeout) as a failure to skip, so an open breaker answers "not breached" without a request and without logging anything about it beyond the exception type.
- **Polly's meter** `Polly` is added to the OpenTelemetry meters, so retries, breaker events and timeouts are visible with the other platform metrics.

## Consequences
- Positive: one tested set of defaults for every outbound call; a service that is down is called at most once per break window per instance, not per request; the retry, timeout and breaker numbers are configuration, not code; a future provider is a two-line registration.
- **A new package, and a Polly dependency that reaches callers.** A caller that must degrade (HIBP does) has to catch `Polly.ExecutionRejectedException` (the base of `BrokenCircuitException` and `TimeoutRejectedException`); the consumer of a client built here sees Polly's types, not only `HttpRequestException`.
- **The breaker is per client and per instance.** Each instance learns separately that a service is down (no shared state, no Redis), and a client's breaker opens for all callers of that client, so an outage seen by one request fails the others fast for the break window.
- **A POST is not retried unless a pipeline opts in.** The standard predicate does not look at the HTTP method, so left alone it would repeat a POST like a GET and could send an SMS or a charge twice. Every pipeline therefore disables the retry for unsafe methods (POST, PUT, PATCH, DELETE, CONNECT; the package's `DisableForUnsafeHttpMethods`), and the pipeline option `RetryUnsafeMethods` (default `false`) turns the retry back on for endpoints that are idempotent or carry an idempotency key the provider honours. The first real consumers (SMS and push providers) POST, so the safe default is the one that matters; the cost is that a transient 503 on a POST fails the call at once. The breaker still counts the failures of unsafe methods.
- **A total timeout that cannot hold the retries cuts them short.** The delays are jittered: five retries wait between about 23 s and 46 s in all, so `external-critical` has a total of 90 s (its attempt stays 10 s). A pipeline of your own that raises `Retries` must raise `TotalTimeout` with it, or the total cuts the last retries short; this is documented where the keys are.
- **A 429 or 408 counts against the breaker** (it is in the standard predicate), so a service that rate-limits us can open its own breaker. This is deliberate: backing off is the right answer to being rate-limited.
- Follow-up actions: a pipeline for the SMS and email providers when they arrive, each with its own name and tests.
