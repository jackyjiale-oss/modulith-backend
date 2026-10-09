# 0016. Server-side permissions with a per-user cache

- Status: Accepted
- Date: 2026-10-08
- Deciders: Lee Jia Le

## Context
Endpoints are protected by permission codes (`module.resource.action`, snake case) that administrators assign to roles and roles to users. A request must be allowed or denied by the user's current permissions, and a change by an administrator should take effect quickly, without waiting for tokens to expire.

## Options considered
1. Permissions as claims in the access token: no lookup per request, but a changed role takes effect only when the token expires, the token grows with the permission list, and permission names leak to the client.
2. Resolve permissions from the database on every request: always current, but one query per authenticated request.
3. Resolve permissions server-side per request through a short-lived per-user cache, removed explicitly when roles or assignments change.

## Decision
We chose option 3.

- The access token carries only the user and session identity (ADR 0015). The authorization handler asks `IPermissionChecker` for the signed-in user's permission codes on each request.
- The checker reads them through `HybridCache` under the key `perm:{userId}` with a **30-second local lifetime**. On a miss it loads the user's role permissions from the `auth` schema in one query.
- Every change to a role's permissions or a user's role assignments removes the key explicitly, so the change is visible at once on this instance and at most 30 seconds later on others.
- `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`) runs in memory today. Plan 5 adds Redis as the distributed layer without changing the callers, which also makes the invalidation reach every instance.
- Permission codes are declared in code and synced into `auth.Permissions` at startup, so the database never invents a code.
- `.RequirePermission(code)` adds one `IAuthorizationRequirement` to the endpoint (D6). There is no dynamic policy provider: the code is the policy.

## Consequences
- Positive: permission changes apply within seconds, not token lifetimes; tokens stay small and free of authorization data; one cache entry per active user keeps the load on SQL Server low.
- Negative / trade-offs accepted: up to 30 seconds of staleness on other instances until Redis is added; a cache miss costs one query; the cache is one more piece of state to reason about.
- Follow-up actions: switch the `HybridCache` backing store to Redis in Plan 5 and test cross-instance invalidation then.
