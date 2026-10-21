# Platform.Redis (`TemplateName.Platform.Redis`)

## Purpose

The optional Redis project ([ADR 0027](../adr/0027-optional-redis-through-one-project.md)). With `Caching:Provider = redis` it makes Redis the shared second level (L2) of `HybridCache`, so a permission set loaded by one API instance is served to the others without a database read; with any other provider it registers nothing. It holds the one Redis connection of the instance, the circuit that keeps a Redis outage away from requests, and the readiness check `redis`. Redis is never required: the default provider is `memory`, and with Redis down the API keeps serving from the database.

It depends on Infrastructure.Common (the `Caching` options), Web.Common, Application.Common, SharedKernel, `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis` and `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (all MIT). **Only the host references it** (`LayeringTests.Only_the_host_references_Platform_Redis`), so a solution that will never use Redis deletes the project and its packages go with it ([below](#remove-the-project-from-a-generated-solution)).

Code: `src/BuildingBlocks/TemplateName.Platform.Redis/`.

## Public types and extension methods

| Extension / type | Does |
|---|---|
| `services.AddPlatformRedis(configuration)` | Does nothing unless `Caching:Provider` is `redis` (it reads the provider while registering; an unknown value fails there, naming `Caching:Provider`). Otherwise: the `Caching:Redis` options and `ConnectionStrings:Redis`, both validated on start; one `IConnectionMultiplexer` for the instance (singleton, connected on first use, `abortConnect=false`, connect, sync and async timeouts of `Caching:Redis:CommandTimeout`, a heartbeat no longer than that timeout and at most one second, and the backlog policy `FailFast`, whatever the connection string says; a `ConnectionStrings:Redis` that does not parse, or names no server, fails on start); the `RedisAvailability` circuit; `AddStackExchangeRedisCache` on that connection with every key prefixed by `Caching:Redis:InstanceName`, its `IDistributedCache` replaced (`services.Replace` with a factory) by `ResilientDistributedCache` around it; and the health check `redis` (failure status `Degraded`, tag `ready`). `HybridCache` uses the registered `IDistributedCache` as its L2 by itself. The host calls it after `AddInfrastructureCommon` and `AddHealthChecks`. |
| `RedisCachingOptions` | `InstanceName`, `FailureThreshold`, `BreakDuration`, `CommandTimeout` (section `Caching:Redis`, [below](#configuration)). |
| `RedisAvailability` | The circuit breaker every Redis caller of the instance shares (singleton): `IsAvailable`, `RecordSuccess()`, `RecordFailure(exception)`, and for the callers in this project `TryBeginCall()` (and `AbandonCall()` for a call its own caller cancelled). After `FailureThreshold` consecutive failures the circuit is open for `BreakDuration` (time from the application's `TimeProvider`) and `TryBeginCall` refuses every call. After the break the circuit is half-open: **one** call, the probe, goes to Redis while every other caller still skips it until the probe reports; its success closes the circuit and resets the count, its failure opens it for another break at once. It logs one warning per outage window (from the circuit opening until the next success, however many breaks that takes) and one information line when Redis answers again. A future Redis caller (a rate limiter, a backplane) records its calls on the same instance. |
| `ResilientDistributedCache` (internal) | The decorator of the Redis L2. It never lets a Redis failure reach the caller: a failed read answers a miss, a failed write, refresh or remove is dropped, and each outcome is recorded on `RedisAvailability`. Every asynchronous call is bounded: one that has not finished after `CommandTimeout` (measured with the application's `TimeProvider`) is given up and counted as a failure. While the circuit is open it does not call Redis at all, and when half-open only its probe does. Only the caller's own cancellation (an `OperationCanceledException` for the token it passed, already cancelled) propagates, and it is not counted as a Redis failure (a probe it held is freed). The synchronous methods rely on the connection's own timeouts; nothing in the template calls them. |
| `RedisHealthCheck` (internal) | During a break of the circuit it answers `Degraded` at once, without calling Redis. Otherwise (closed, or half-open after the break) it sends one `PING` on the shared connection and gives up after `CommandTimeout`, so a check never takes longer than that. It does not report to the circuit or take its probe. |

## Configuration

`Caching:Provider` belongs to Infrastructure.Common ([infrastructure-common](infrastructure-common.md#caching)); the keys below are read only when it is `redis`. All of them are listed with defaults in [API host: `Caching`](../services/api.md#caching).

| Key | Default | Validation (on start, provider `redis` only) | Meaning |
|---|---|---|---|
| `ConnectionStrings:Redis` | empty | required | A StackExchange.Redis configuration string, for example `localhost:6379` or `redis.internal:6380,password=…,ssl=true`. A password belongs in user secrets or the environment (`ConnectionStrings__Redis`). `abortConnect` and the timeouts in it are overridden (below). |
| `Caching:Redis:InstanceName` | `TemplateName:` | required | The prefix of every key, so applications can share one server. |
| `Caching:Redis:FailureThreshold` | `3` | 1 to 100 | Consecutive failed Redis calls that open the circuit. |
| `Caching:Redis:BreakDuration` | `00:00:30` | `00:00:01` to `01:00:00` | How long Redis is skipped once the circuit opens. |
| `Caching:Redis:CommandTimeout` | `00:00:00.500` | `00:00:00.050` to `00:00:30`, and shorter than `BreakDuration` (so a call cannot outlive a break and free the probe slot while a second probe runs) | The connect, synchronous and asynchronous timeouts of the connection, and the bound the cache and the readiness check put on every asynchronous call (and on each `PING`): the most a request or a check waits for a Redis that does not answer before the call counts as a failure. The heartbeat, on which StackExchange.Redis checks for timed-out commands, is shortened to it (at most one second), and while the connection is down commands fail at once (`FailFast`) instead of queueing for the reconnect. Without a short timeout a dead Redis would add seconds to every request until the circuit opens. |

A host whose `Caching:Provider` is `redis` but which does not call `AddPlatformRedis` fails on start (`Caching:Provider is redis, but no distributed cache is registered`) instead of caching in memory only without saying so.

### The shared connection

The instance has one `IConnectionMultiplexer`, created and owned by the container, which disposes it when the host stops. The cache and the readiness check use it (and, as they are added, the rate limiter and the SignalR backplane). `RedisCache.Dispose` closes and disposes the connection it got from its factory, so the decorator never disposes the inner `RedisCache` (`CachingProviderTests.Disposing_the_cache_does_not_close_the_shared_connection`). For the same reason the StackExchange.Redis AppContext switch `Microsoft.AspNetCore.Caching.StackExchangeRedis.UseForceReconnect` must stay off: with it on, `RedisCache.OnRedisError` releases (closes) the connection after errors, which would close the shared connection under the other users.

## Keys

`{InstanceName}{module}:{entity}:{id}`, with no tenant (the template has none). The module writes `{module}:{entity}:{id}`; `InstanceName` is added by the Redis cache. Today there is one entry: the Auth module's permission set, `auth:perm:{userId}` in the module, `TemplateName:auth:perm:{userId}` in Redis ([Auth module](../modules/auth.md#permission-checks)). A module adding a cached entry follows the same shape, so two modules never collide and `redis-cli --scan --pattern 'TemplateName:auth:*'` finds one module's keys.

## Degradation

Redis is a cache here, so losing it costs speed, not correctness (spec section 4):

| When | What happens |
|---|---|
| Redis is down when the host starts | The host starts (`abortConnect=false`); the multiplexer keeps reconnecting in the background. Readiness is `Degraded`. |
| Redis stops answering while the host runs | While the connection is down (refused, reset) an L2 call fails at once (`FailFast`); while it still looks up but Redis does not answer, the call is given up after `CommandTimeout`. Either way it answers a miss (a read) or is dropped (a write, refresh, remove). The value comes from the database. After `FailureThreshold` consecutive failures the circuit opens and requests stop waiting on Redis altogether for `BreakDuration`. One warning is logged for the outage. |
| Readiness during the outage | `/health/ready` answers **200 `Degraded`**, not 503 `Unhealthy`, at once during a break of the circuit and within `CommandTimeout` otherwise: the instance still serves, and a load balancer must not drain every instance for a cache outage. Alert on `Degraded` instead. |
| Redis comes back | The multiplexer reconnects by itself. When the break is over, one probe call goes to Redis (the others still miss to the database until it reports); its success closes the circuit and an information line is logged, its failure starts another break. Readiness does not ping during a break, so it stays `Degraded` for up to `BreakDuration` after Redis is back; from the end of the break it pings again and turns `Healthy` on the first `PING` that succeeds. |
| A removal fails, or is skipped because the remover's circuit is open | The entry stays in Redis until its own expiration (at most 30 seconds for a permission set). Another instance whose first level expires meanwhile reads the old value from Redis and keeps it for another first-level lifetime, so the stale window is up to about **twice** the lifetime (the remaining Redis time plus one first-level lifetime: about 60 seconds for a permission set). Removals that fail are not retried. |

The first-level cache (`IMemoryCache`, per instance) keeps working throughout, so an instance that has a value keeps using it for its local lifetime whether Redis is up or not.

## Cross-instance invalidation: the honest limit

`HybridCache.RemoveAsync` clears the entry in Redis and in the first level of the instance that calls it. It does **not** reach the first level of the other instances: nothing tells them. Another instance that read the entry before the removal keeps serving it until its local copy expires. For a permission set that is at most **30 seconds** (`PermissionCache.Lifetime`, both levels): a user who loses a permission can keep using it on another instance for up to 30 seconds after the change. This is the limit the blueprint review (2.7) and [ADR 0016](../adr/0016-server-side-permissions-with-per-user-cache.md) accept; `RedisCacheTests.Invalidating_on_A_is_seen_by_B_after_the_thirty_second_L1_lifetime` pins it to the millisecond. If the removal itself does not reach Redis (Redis failed, or the remover's circuit was open), the window grows to about twice the lifetime, up to 60 seconds: the old set stays in Redis for what is left of its 30 seconds, and an instance that reads it then keeps it for another 30 ([Degradation](#degradation)). Removing the stale window needs pub/sub invalidation of the first level, which is not built (post-v1). Without Redis the same 30 seconds apply, with each instance also reading the database on its own.

## Running Redis locally

Docker Compose has a `valkey` service (Valkey: BSD-3, Redis-protocol compatible; ADR 0027) behind the profile `redis`, published on `127.0.0.1:6379` only (it has no password). Its image tag is pinned and matches `ValkeyFixture.Image` (a test checks it).

```bash
docker compose --profile redis up -d
Caching__Provider=redis ConnectionStrings__Redis=localhost:6379 dotnet run --project src/Host/TemplateName.Api
```

`docker compose up -d` without the profile does not start it.

## Remove the project from a generated solution

A solution that will never use Redis can delete the project; nothing else references it.

1. Delete `src/BuildingBlocks/TemplateName.Platform.Redis/` and its line in `TemplateName.slnx`.
2. In `src/Host/TemplateName.Api`: remove the project reference from the `.csproj`, and the `AddPlatformRedis` call and its `using` from `Program.cs`. Remove `ConnectionStrings:Redis` and `Caching:Redis` from `appsettings.json` (keep `Caching:Provider`; `memory` and `none` still work).
3. In the tests: remove the project reference from `TemplateName.UnitTests` and `TemplateName.ArchitectureTests`; delete `tests/TemplateName.UnitTests/Platform/` and `tests/TemplateName.IntegrationTests/Redis/` and `Infrastructure/ValkeyFixture.cs`; remove `PlatformRedis` from `Assemblies.cs`, `platform-redis` from `DocumentationTests.Every_building_block_has_a_document`, and the test `LayeringTests.Only_the_host_references_Platform_Redis`. Remove the `Testcontainers.Redis` reference from the integration tests.
4. Remove `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis` and `Testcontainers.Redis` from `Directory.Packages.props`, the `valkey` service from `docker-compose.yml`, this document and its row in `docs/README.md`, then run `dotnet restore --force-evaluate` to update the lock files.

`Caching:Provider = redis` then fails on start with a message naming `AddPlatformRedis`.

## Tests

- Unit (`tests/TemplateName.UnitTests/Platform/`): `ResilientDistributedCacheTests` (a failing inner cache never throws through read, write, refresh or remove; three failures keep it uncalled for 30 seconds; it is called again after the break; one failure after the break opens it again; a success resets the count; a cancelled call propagates and is not a failure; after the break only one probe reaches Redis until it reports, a successful probe closes the circuit for every caller and a cancelled probe frees the slot; a call that hangs is given up after exactly `CommandTimeout`), `RedisHealthCheckTests` (`Healthy` on a `PING`, `Degraded` after exactly `CommandTimeout` when the `PING` hangs, `Degraded` at once without calling Redis during a break, a `PING` again after the break without taking the cache's probe), `RedisAvailabilityTests` (threshold, one warning per outage window, the recovery line), `CachingProviderTests` (the provider default and parsing, `none` disabling both layers, start-up validation of the connection string and of every option, the timeouts, the shortened heartbeat and `FailFast` reaching the connection, a malformed connection string failing on start, disposing the cache not closing the shared connection, a host with `redis` and no `AddPlatformRedis`, the permission key `auth:perm:{userId}`).
- Integration (`tests/TemplateName.IntegrationTests/Redis/`, Valkey through `ValkeyFixture`, two hosts from `Factory.WithWebHostBuilder` sharing the SQL database, the clock and one Valkey): a set cached by host A is served to host B without a database read; an invalidation on A is seen by B after exactly the 30-second first-level lifetime; with Valkey stopped the API still checks permissions (from the database) and readiness is 200 `Degraded`; with Valkey stopped three cache reads and three readiness checks each take well under the 1.5 s the old queued commands needed (`Valkey_stopped_readiness_and_cache_reads_do_not_wait_for_Redis`, ceiling 1.2 s for three); a host started while Valkey is down serves from the database and then through Redis once Valkey is back and the break is over; with Valkey back the cache recovers after the break. `Caching/CachingProviderTests` covers `none` (every permission read reaches the database, although the checker passes its own entry options) and the default `memory` (no `IConnectionMultiplexer`, no `redis` check).
- `ValkeyFixture` publishes the container on a fixed host port chosen free when it starts, so a test that stops and starts it gets it back at the same address and the hosts reconnect by themselves; the Redis tests share it as a collection fixture (`RedisCollection`) and every test that stops it starts it again in a `finally`.
