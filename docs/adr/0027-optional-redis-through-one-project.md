# 0027. Optional Redis through one project, our own rate limiter, Valkey in compose

- Status: Accepted
- Date: 2026-10-21
- Deciders: Lee Jia Le

## Context
Until Plan 5 every cache and counter lived in the memory of one instance: the permission cache (`HybridCache` with no second level), the rate-limit counters, the SignalR connections and the per-user hub ticket limit. A template user who runs the API on two or more instances gets a database read per instance for every permission set, `instances × limit` requests per client, and pushes that only reach the connections of the instance that sent them.

Redis fixes all three, but most template users start on one instance and should not have to run, secure or pay for Redis to start. And Redis can fail: a shared cache that takes the API down with it is worse than no shared cache. The design of Plan 5 (decisions D4 to D6) settles where Redis lives, how the rate limiter uses it and what local development runs.

## Options considered
1. **Redis inside the existing building blocks** (Infrastructure.Common and Web.Common reference `StackExchange.Redis` and switch on configuration). One project less, but every solution carries the Redis packages, also the ones that never use it, and the Redis code is mixed into blocks every module depends on.
2. **One Redis project per concern** (a cache project, a rate-limit project, a backplane project). Each can be dropped alone, but they share one connection and one view of whether Redis is up, so they would need a fourth project for that; three projects for one dependency.
3. **One optional project, `TemplateName.Platform.Redis`, referenced only by the host**, holding the connection, the cache's second level, the Redis rate limiter and the SignalR backplane, all switched on by `Caching:Provider = redis`.

For the distributed rate limiter (review 2.11): the built-in ASP.NET Core limiter has no Redis backend, so the options are a community package, dividing every limit by the number of instances, or a small limiter of our own.

For local development: the Redis server image, or a Redis-protocol-compatible server under an open-source licence.

## Decision
We chose option 3 (decision D4), our own fixed-window limiter (D5) and Valkey in Docker Compose (D6).

- **One project, only the host references it.** The rule of the template is that a separate project exists only when excluding it removes a NuGet dependency; deleting `TemplateName.Platform.Redis` removes `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis` and `Microsoft.AspNetCore.SignalR.StackExchangeRedis`. An architecture test (`LayeringTests.Only_the_host_references_Platform_Redis`) keeps every other project off it, so deleting it touches the host, the tests and the packages only (`platform-redis.md` lists the steps). `Caching:Provider` itself (`memory`, the default; `redis`; `none` to switch the cache off for diagnosing) lives in Infrastructure.Common, so a host without the project still honours `none` and refuses `redis` on start rather than quietly caching in memory.
- **Degradation is built, not assumed.** With `redis`, the project registers one `IConnectionMultiplexer` with `abortConnect=false` (a Redis that is down at start does not stop the host) and a short command timeout (`Caching:Redis:CommandTimeout`, 500 ms, for connect, sync and async calls, with a heartbeat no longer than it and the `FailFast` backlog policy so a down connection fails a command at once), bounds every asynchronous cache call and readiness `PING` by that timeout, and wraps the `HybridCache` second level in a decorator that never throws a Redis failure: a read misses to the database, a write is dropped. After `Caching:Redis:FailureThreshold` (3) consecutive failures a shared circuit (`RedisAvailability`) skips Redis for `Caching:Redis:BreakDuration` (30 s), so requests stop paying the timeout; after the break a single probe call tests Redis while the other callers keep missing to the database. One warning is logged per outage. The readiness check `redis` fails as `Degraded`, not `Unhealthy` (at once during a break, within the command timeout otherwise), so a load balancer keeps serving from instances whose only problem is the cache.
- **Keys** are `{InstanceName}{module}:{entity}:{id}` (`TemplateName:auth:perm:{userId}`), with no tenant.
- **Invalidation across instances is honest.** A removal clears Redis and the first level of the instance that removes; other instances keep their first-level copy for at most its lifetime (30 s for a permission set, the window ADR 0016 accepted). A removal that does not reach Redis (Redis failed, or the remover's circuit was open) leaves the old value there for the rest of its lifetime, so the window can grow to about twice the lifetime. Pub/sub invalidation of the first level is a post-v1 item.
- **The rate limiter is ours** (D5): a fixed-window limiter on Redis (one Lua script, `INCR` and `PEXPIRE`), behind an `IFixedWindowLimiterFactory` that Web.Common defines with the in-memory limiter as its default. On a Redis failure or an open circuit the request is counted by the in-memory limiter. It is about 100 lines with tests; a community package would add a maintenance and licence risk for a feature this small.
- **Valkey in compose** (D6): a `valkey` service (BSD-3, Redis-protocol compatible) behind the profile `redis`, published on loopback only, its tag pinned and checked against the integration tests' `ValkeyFixture`. Redis 7.4 and later are licensed RSAL, SSPL or AGPL, which the template's licence rule does not allow for what it pulls; the client packages are MIT either way, and production can point `ConnectionStrings:Redis` at any Redis-protocol server.

## Consequences
- Positive: one switch (`Caching:Provider = redis` plus `ConnectionStrings:Redis`) shares the permission cache across instances, and the same project carries the limiter and the backplane; a solution that never needs Redis deletes one project; a Redis outage costs database reads and a warning, not requests.
- **A second place to look.** Redis-specific code is not in the building block that defines the abstraction (the limiter factory in Web.Common, the caching options in Infrastructure.Common): a reader follows the host's `AddPlatformRedis` call to find it.
- **A stale permission set for up to 30 s on other instances** after a change, as before Redis, now documented next to the setting.
- **Our own limiter to maintain**, and while Redis is down every instance limits on its own, so the effective limit is `instances × limit` until Redis returns.
- **The circuit is per instance.** Each instance learns separately that Redis is down; until its own circuit opens, a request can wait up to the command timeout once per Redis call while Redis hangs (a refused connection fails at once).
- **Valkey, not Redis, in development.** A feature only Redis has (none used today) would not work locally; swapping the image line is the way back.
- Follow-up actions: pub/sub invalidation of the first level (post-v1).
