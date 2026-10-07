# 0008. Literal `/api/v1` routes until a breaking change requires versioning

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Public HTTP APIs need a way to evolve without breaking existing clients. The blueprint proposed URL-segment versioning through a library (`Asp.Versioning`) from the start. That adds a package, per-endpoint version metadata, and a versioned OpenAPI document pipeline, all before any endpoint has needed a second version. The template ships a single API version and no consumers yet (blueprint review, Section 4).

## Options considered
1. `Asp.Versioning` URL-segment versioning from the start — ready for `v2`, supports deprecation headers and per-version OpenAPI documents; but adds a dependency and configuration that nothing uses yet.
2. A literal `/api/v1` route group (`app.MapGroup("/api/v1")`) with a single OpenAPI document named `v1` — nothing to configure, no extra package; adding versioning later means introducing the library and moving endpoints under versioned groups.
3. Unversioned routes (`/api/...`) — shortest URLs; but the first breaking change would then change the URL shape that existing clients already use.

## Decision
We chose option 2. Every module endpoint is mapped under the literal `/api/v1` group in `Program.cs`, and the OpenAPI document is registered as `v1`. The first breaking change adds `Asp.Versioning` and a `v2` group. Because `/api/v1/...` is already the URL shape, existing clients keep working and the route layout stays the same.

## Consequences
- Positive: no versioning package or configuration to maintain; routes read the same before and after versioning is adopted.
- Negative / trade-offs accepted: when `v2` arrives, we must add the library, register version sets, and add a second OpenAPI document; until then there is no built-in deprecation or sunset signalling.
- Follow-up actions: when the first breaking endpoint change is proposed, write a new ADR that adopts `Asp.Versioning` and supersedes this one.
