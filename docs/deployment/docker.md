# Docker

The API image, how to build and run it, and how to roll it back. The pipeline that publishes it is in [`README.md`](README.md); the decisions are in [ADR 0033](../adr/0033-delivery-pipeline.md).

None of the commands on this page had been run against a real Docker daemon when it was written (the daemon on the developer machine was unavailable); the `container` job of `ci.yml` is their first execution.

## The image

`build/docker/Dockerfile`, built from the repository root:

- **Build stage** `mcr.microsoft.com/dotnet/sdk:10.0`: copies the project files and `packages.lock.json` of the projects the API references, runs `dotnet restore --locked-mode`, then copies `src/` and runs `dotnet publish`. The restore layer is cached until a project file, a lock file, a `Directory.*` file, `global.json` or `Version.props` changes. The test projects and the solution file are left out on purpose. **Add a `COPY` line when a project is added to the API's references**; the restore fails loudly if one is missing.
- **Runtime stage** `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`: no shell, no package manager, a non-root user (`USER $APP_UID`). The `-extra` variant carries ICU and tzdata, which the `ms` and `zh-Hans` resources and the locale and time-zone validation need.
- **Entry point** `dotnet TemplateName.Api.dll`; the same binary has two one-shot modes, `migrate` ([`migrations.md`](migrations.md)) and `healthcheck`.

Nothing is baked in: no secret and no environment-specific setting. Configuration is environment variables (`Section__Key`, arrays `Section__Key__0`), as in [`services/api.md`](../services/api.md#configuration).

```bash
docker build -f build/docker/Dockerfile -t api:local .
```

The build uses a moving base tag, and NuGet Audit runs in the restore, so a newly disclosed advisory can fail the build with no code change of yours; bump the package.

## Running it

Migrate first (the API does not migrate on start outside Development), then start it:

```bash
# ConnectionStrings__Database for migrate is the DDL login (migrations.md); the API gets the runtime login.
docker run --rm -e ConnectionStrings__Database api:local migrate

docker run -d --name api -p 8080:8080 \
  -e ConnectionStrings__Database \
  -e Auth__Jwt__SigningKeys__0__KeyId \
  -e Auth__Jwt__SigningKeys__0__PrivateKeyPem \
  api:local
```

`-e NAME` with no value takes the value from your shell, so the secret is on no command line and in no file. The settings above are the minimum for Production; the full list of keys and their defaults is [`services/api.md`](../services/api.md#configuration), and the front-end URLs in `Auth:Links`, the SMTP settings in `Notifications:Email`, `AllowedHosts`, `Cors` and `ForwardedHeaders` are what a real deployment sets as well. The signing key is an EC P-256 private key in PEM ([Auth: access tokens](../modules/auth.md#access-tokens)).

**Port.** The image listens on HTTP 8080 only: `ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`. TLS ends at the proxy or load balancer. To change the port set **`ASPNETCORE_HTTP_PORTS`**, not `ASPNETCORE_URLS`: the health check reads only the first port number of `ASPNETCORE_HTTP_PORTS` (8080 when it is unset or holds no port number), so a port set only through `ASPNETCORE_URLS` leaves the container reported unhealthy.

**Do not set `HTTPS_PORT`, `ASPNETCORE_HTTPS_PORT(S)` or an https URL in the container.** Outside Development the host enables HTTPS redirection, and once an https port is known it redirects plain-HTTP requests to it. The health check does not follow the redirect, so a healthy container would report `unhealthy (307)`. Let the proxy do TLS and forward the scheme (below); the application then sees `https` and sends HSTS.

**`AllowedHosts`.** Set it to the public host names **and `127.0.0.1`**, for example `AllowedHosts=api.example.com;127.0.0.1;localhost`. Host filtering answers 400 to any other `Host` header, and the image's health check sends `Host: 127.0.0.1:<port>`: with the public names alone a healthy container reports `unhealthy (400)` and an orchestrator restarts it. Add any host name your load balancer's probe sends as well. `container-verify.sh` starts the image with this shape (`api.example.test;127.0.0.1;localhost`), so CI proves the probes pass with it.

**State.** Nothing is written to the container's file system that matters: the Data Protection key ring is in the database (`auth.DataProtectionKeys`, ADR 0017), logs go to the console as compact JSON, and the outbox, inbox and jobs are tables.

## The health check

The image declares `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3`, running `dotnet TemplateName.Api.dll healthcheck`. That mode builds no host and makes one GET to `http://127.0.0.1:<port>/health/live` with a 3 second timeout; it prints one line (`healthy (200)`, `unhealthy (503)`, `unhealthy (307)` for a redirect, which it does not follow, `unhealthy (400)` when `AllowedHosts` leaves out `127.0.0.1`, `unhealthy (TaskCanceledException)`) and exits 0 on a 2xx and 1 otherwise. It probes `/health/live`, which checks nothing but that the process answers, so a database outage does not make Docker or an orchestrator restart healthy containers. Use `/health/ready` (the database, and Redis when enabled) for a load balancer and for the smoke test after a deploy ([`services/api.md`](../services/api.md#health)).

An orchestrator that has its own probes (Kubernetes) points them at `/health/live` and `/health/ready` and may ignore the image's `HEALTHCHECK`.

## Behind a proxy or load balancer

The application sits behind a TLS-terminating proxy. Set `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks` (`ForwardedHeaders__KnownNetworks__0=10.0.0.0/24`) to the proxy's address or network. With both empty only loopback proxies are trusted, so a proxy on another address has its `X-Forwarded-For` and `X-Forwarded-Proto` ignored: every request then looks like it came from the proxy over plain HTTP, and the rate limiter partitions every caller on the proxy's one address. Make the proxy send `X-Forwarded-For` and `X-Forwarded-Proto`; the behaviour and its limits are in [`services/api.md`](../services/api.md#forwardedheaders). Set `AllowedHosts` to the public host names plus `127.0.0.1` ([above](#running-it)).

Point the proxy's health probe at `/health/ready`, and give it a path that does not need a token (both health routes are anonymous and exempt from rate limiting).

## Verifying an image by hand

```bash
bash build/scripts/container-verify.sh api:local      # or ghcr.io/<owner>/<repository>/api@sha256:...
```

It needs `docker`, `openssl` and `curl`, and takes a few minutes (`VERIFY_TIMEOUT`, default 120 seconds per wait). It generates an SA password and a signing key for the run and writes neither to a file. Seven checks: start a throwaway SQL Server on a private network; run `migrate` twice (the first retried while the server starts); start the image in Production with the minimum configuration; wait for the image's own `HEALTHCHECK` to say `healthy`; call `/health/ready` and expect 200; assert the configured user is not root; remove every container and the network, printing the logs first on a failure. Everything it creates carries the label `container-verify=<pid>`; `docker ps -a --filter label=container-verify` and `docker network ls --filter label=container-verify` should print nothing afterwards (a `SIGKILL` cannot be cleaned up by the script; remove what it leaves with those filters).

What the script cannot rule out: it starts the image against an empty database, so the outbox, the recurring jobs, the delivery worker and the seeding run but have nothing to do, and a problem that appears only with data is not seen. The integration tests that start Production switch those services off.

## Rolling back

A container rollback is **redeploying the previous image**: the previous release tag (`x.y.z`) or, better, the previous digest, through the same `roll-out.sh` your project wrote. There is no automatic rollback. The database is not rolled back: every migration is expand-only (ADR 0031), so the previous version runs on the new schema. If a release contracted a column (`[ContractMigration]`), that guarantee ends there; see [`migrations.md`](migrations.md#expand-then-contract). Find the previous digest in the package's version list on GHCR or in the previous `cd.yml` run (`image` job output).
