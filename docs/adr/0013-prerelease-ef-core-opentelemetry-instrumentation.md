# 0013. Prerelease OpenTelemetry instrumentation for EF Core

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Every package uses its latest stable version. Prerelease versions are allowed only for the OpenTelemetry EF Core and SqlClient instrumentation, and only when an ADR records them. Tracing database work is part of the observability baseline: with OTLP export on, each EF Core command should appear as a span under the request that caused it. `OpenTelemetry.Instrumentation.SqlClient` has a stable release (1.19.0), but `OpenTelemetry.Instrumentation.EntityFrameworkCore` has never had one; its newest version is `1.19.1-beta.1`.

## Options considered
1. No EF Core instrumentation: only stable packages, but EF Core commands lose their EF-level spans (operation, context) and only the lower-level SqlClient spans remain.
2. Write our own `DiagnosticListener` for EF Core events: no prerelease package, but code to maintain that the OpenTelemetry project already provides.
3. Use the prerelease `OpenTelemetry.Instrumentation.EntityFrameworkCore`, pinned in `Directory.Packages.props`.

## Decision
We chose option 3.

- `OpenTelemetry.Instrumentation.EntityFrameworkCore` `1.19.1-beta.1` is the **only prerelease dependency**. `Directory.Packages.props` marks it with a comment, and `AddObservability` (Web.Common) registers it with `AddEntityFrameworkCoreInstrumentation()` only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- The SqlClient instrumentation stays on its stable version (`1.19.0`).
- No other prerelease package is allowed; a new one needs its own ADR.

## Consequences
- Positive: EF Core spans in every trace when export is on, with no custom code.
- Negative / trade-offs accepted: a beta package may change its API or span attributes between versions; it is loaded only when OTLP export is configured, which limits the exposure.
- Follow-up actions: move to the first stable release as soon as one ships (Dependabot proposes it), and mark this ADR superseded then.
