#!/usr/bin/env bash
# Verifies an API image, the one set of checks CI (ci.yml, job `container`) and CD (cd.yml, on the pushed digest) both run:
#   1. start a throwaway SQL Server on a private Docker network, with a password generated for this run;
#   2. run `<image> migrate` against it, retried while the server starts, then run it again: both must exit 0;
#   3. start `<image>` as the API in Production with the minimum configuration listed below and a non-wildcard AllowedHosts;
#   4. wait until Docker reports the image's own HEALTHCHECK as `healthy`;
#   5. GET /health/ready from this machine: 200;
#   6. the container's configured user is not root;
#   7. remove every container and the network, whatever the outcome; on failure print the container logs first.
#
# Usage: bash build/scripts/container-verify.sh <image>      for example api:ci, or ghcr.io/owner/repo@sha256:...
#
# Environment:
#   VERIFY_TIMEOUT    seconds each wait may take (SQL Server ready, the migrate retries, healthy, ready); default 120.
#   VERIFY_SQL_IMAGE  the SQL Server image; default mcr.microsoft.com/mssql/server:2022-latest, as docker-compose.yml and the tests.
# Needs docker, openssl and curl. Exits 0 only when every check passes.
#
# Clean-up: every container and the network it creates carry the label container-verify=<pid of this script> and are removed on
# exit, normal or not (EXIT, INT, TERM), so after any run that has finished both of these print nothing:
#   docker ps -a --filter label=container-verify -q
#   docker network ls --filter label=container-verify -q
# A SIGKILL cannot be trapped; remove what it leaves with the same label filters.
#
# Secrets: the SA password (`openssl rand -hex 12` plus `Aa1!`, for SQL Server's complexity rule) and the ES256 signing key are made
# here for this run only and never written to a file. When GITHUB_ACTIONS is set both are masked before any use. They reach a
# container by name (`-e NAME`, taken from the environment of that one docker command), never on a command line, so the runner's
# process list does not show them.
#
# The minimum Production configuration (docs/deployment/docker.md, "Running it"; ADR 0033). Production refuses to start without:
#   ConnectionStrings__Database                    ConnectionStringsOptions.Database is [Required], validated on start; `migrate`
#                                                  reads it too (ADR 0006), and is given nothing else.
#   Auth__Jwt__SigningKeys__0__KeyId               JwtOptions: an empty Auth:Jwt:SigningKeys fails ValidateOnStart outside Development and
#   Auth__Jwt__SigningKeys__0__PrivateKeyPem       Testing; the key must be EC P-256, PEM 'PRIVATE KEY' (PKCS#8) or 'EC PRIVATE KEY'.
# ASPNETCORE_ENVIRONMENT=Production is set explicitly (the image's default is Production too).
# AllowedHosts is set the way the docs tell a deployment to set it, not left at `*`: a public host name plus 127.0.0.1 (the
# image's HEALTHCHECK sends Host 127.0.0.1:<port>) and localhost. Host filtering answers 400 to any other Host, so steps 4 and 5
# (the HEALTHCHECK and the curl to 127.0.0.1) prove that this documented shape keeps the probes working.
# Everything else starts in Production with the defaults of appsettings.json, read from the options classes and the module
# registrations (AddInfrastructureCommon, AddHttpSecurity, AddAuthModule, AddNotificationsModule, AddAuditModule, AddPlatformRedis):
#   - the only environment-dependent checks in src/ are the signing key (above), HSTS plus HTTPS redirection outside Development
#     (with no https port configured, UseHttpsRedirection only logs a warning and passes the request on; HSTS acts on https requests
#     only; the Dockerfile says not to set an https port), OpenAPI and Scalar (not mapped in Production) and the log format;
#   - Auth:Links: the localhost defaults pass (absolute http(s) with {token}); a real deployment sets its front-end URLs;
#   - Notifications:Email: host localhost and no username pass; SMTP is reached only for a pending email delivery, none here;
#     no key may exist under Auth:Email (LegacyEmailSettingsCheck);
#   - Cors (empty list), ForwardedHeaders (empty lists), RateLimiting, Outbox, Idempotency, Retention, Jobs, Caching (memory, so no
#     Redis), Localization, Auth:Password/Verification/RefreshToken/Lockout/Mfa/StepUp/Retention, Auth:External (every provider
#     disabled), Notifications:Templates/Delivery/Hub/Retention, Audit: valid defaults;
#   - Data Protection: the key ring is kept in auth.DataProtectionKeys (made by `migrate`), not on the container's file system;
#   - Auth:Seed:RunOnStartup (on by default) seeds roles and permissions before the host starts, so the database must be migrated
#     first (step 2); with Auth:Seed:AdminEmail/AdminPassword empty no administrator is created;
#   - OTEL_EXPORTER_OTLP_ENDPOINT unset: no exporter.
# The integration test HostTests.Scalar_is_not_mapped_in_production starts Production with the same files plus a signing key.
# Not ruled out without running the image: that test switches the outbox dispatcher, the recurring jobs and the delivery worker
# off and skips the seeding, while the container runs all of them; a background service that throws stops the host (and fails
# step 4 here, with the logs printed).
set -euo pipefail

readonly sql_image="${VERIFY_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
readonly timeout="${VERIFY_TIMEOUT:-120}"
readonly label="container-verify=$$"
readonly prefix="container-verify-$$"
readonly network="$prefix"
readonly sql="$prefix-sql"
readonly api="$prefix-api"
readonly api_port=8080
work=""

step() {
  echo
  echo "==> $1"
}

pass() {
  echo "PASS: $1"
}

# Exits the script (the EXIT trap cleans up). Never call it inside $( ), where it would only leave the subshell.
fail() {
  echo "FAIL: $1" >&2
  exit 1
}

# One field of `docker inspect`, or "missing" when the object is gone.
inspect() {
  docker inspect --format "$2" "$1" 2>/dev/null || echo missing
}

print_diagnostics() {
  echo
  echo "==> Diagnostics" >&2
  if [ -n "$work" ] && [ -s "$work/migrate.log" ]; then
    echo "--- output of the last migrate run" >&2
    cat "$work/migrate.log" >&2
  fi
  if docker inspect "$api" >/dev/null 2>&1; then
    echo "--- state of $api: $(inspect "$api" '{{.State.Status}} (exit code {{.State.ExitCode}})')" >&2
    echo "--- HEALTHCHECK results of $api" >&2
    inspect "$api" '{{if .State.Health}}{{range .State.Health.Log}}exit {{.ExitCode}}: {{.Output}}{{end}}{{else}}none{{end}}' >&2
    echo "--- logs of $api" >&2
    docker logs "$api" >&2 2>&1
  fi
  if [ -n "$work" ] && [ -s "$work/ready.body" ]; then
    echo "--- body of the last /health/ready answer" >&2
    cat "$work/ready.body" >&2
    echo >&2
  fi
  if docker inspect "$sql" >/dev/null 2>&1; then
    echo "--- last 50 log lines of $sql" >&2
    docker logs --tail 50 "$sql" >&2 2>&1
  fi
}

# Removes everything labelled for this run: containers (with their anonymous volumes), then the network.
remove_created() {
  local containers=() networks=()
  mapfile -t containers < <(docker ps -aq --filter "label=$label" 2>/dev/null)
  if [ "${#containers[@]}" -gt 0 ]; then
    docker rm -f -v "${containers[@]}" >/dev/null
  fi
  mapfile -t networks < <(docker network ls -q --filter "label=$label" 2>/dev/null)
  if [ "${#networks[@]}" -gt 0 ]; then
    docker network rm "${networks[@]}" >/dev/null
  fi
}

# Runs once: on exit, or on INT/TERM with the conventional status (128 + signal), so an interrupted run never exits 0.
cleanup() {
  local status=$?
  if [ "$#" -gt 0 ]; then
    status=$1
  fi
  trap - EXIT INT TERM
  set +e
  # $work is set just before the first docker object is created; before that (usage, a missing tool) there is nothing to show or remove.
  if [ -n "$work" ]; then
    if [ "$status" -ne 0 ]; then
      print_diagnostics
    fi
    remove_created
    rm -rf "$work"
  fi
  exit "$status"
}
trap 'cleanup' EXIT
trap 'cleanup 130' INT
trap 'cleanup 143' TERM

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "Usage: bash build/scripts/container-verify.sh <image>" >&2
  exit 2
fi
readonly image="$1"

if ! [[ "$timeout" =~ ^[1-9][0-9]*$ ]]; then
  fail "VERIFY_TIMEOUT must be a whole number of seconds, greater than 0."
fi
for tool in docker openssl curl; do
  command -v "$tool" >/dev/null 2>&1 || fail "$tool is required."
done

work="$(mktemp -d)"

# The secrets of this run, masked before any use.
sa_password="$(openssl rand -hex 12)Aa1!"
signing_key_pem="$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)"
if [ -n "${GITHUB_ACTIONS:-}" ]; then
  echo "::add-mask::$sa_password"
  while IFS= read -r line; do
    case "$line" in
      "" | -----*) ;;
      *) echo "::add-mask::$line" ;;
    esac
  done <<<"$signing_key_pem"
fi
connection_string="Server=$sql,1433;Database=Verify;User Id=sa;Password=$sa_password;Encrypt=True;TrustServerCertificate=True"

echo "Verifying $image (each wait up to ${timeout}s; label $label)"

step "1/7 Starting SQL Server ($sql_image) on the private network $network"
docker network create --label "$label" "$network" >/dev/null
MSSQL_SA_PASSWORD="$sa_password" docker run -d --name "$sql" --label "$label" --network "$network" \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD \
  "$sql_image" >/dev/null
deadline=$((SECONDS + timeout))
while :; do
  sql_logs="$(docker logs "$sql" 2>&1 || true)"
  case "$sql_logs" in
    *"SQL Server is now ready for client connections"*) break ;;
  esac
  sql_state="$(inspect "$sql" '{{.State.Status}}')"
  if [ "$sql_state" != running ]; then
    fail "SQL Server stopped before it was ready (state: $sql_state)."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "SQL Server was not ready after ${timeout}s."
  fi
  sleep 2
done
pass "SQL Server is ready"

# The migrate mode exits 1 on any failure, an unreachable server or a login refused while SQL Server finishes starting included,
# so 1 is retried until the timeout; any other code (125 to 127: Docker could not run the image or the command) fails at once.
run_migrate() {
  ConnectionStrings__Database="$connection_string" docker run --rm --label "$label" --network "$network" \
    -e ASPNETCORE_ENVIRONMENT=Production -e ConnectionStrings__Database \
    "$image" migrate >"$work/migrate.log" 2>&1
}

step "2/7 Running '$image migrate', twice"
deadline=$((SECONDS + timeout))
attempt=0
while :; do
  attempt=$((attempt + 1))
  if run_migrate; then
    break
  else
    migrate_status=$?
  fi
  if [ "$migrate_status" -ne 1 ]; then
    fail "migrate exited $migrate_status on attempt $attempt; only 1 (the migrate mode's failure) is retried."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "migrate still failed after ${timeout}s ($attempt attempts)."
  fi
  sleep 3
done
pass "migrate exited 0 (attempt $attempt)"
if run_migrate; then
  pass "migrate exited 0 again (idempotent)"
else
  fail "the second migrate exited $? instead of 0."
fi

step "3/7 Starting '$image' as the API in Production"
ConnectionStrings__Database="$connection_string" Auth__Jwt__SigningKeys__0__PrivateKeyPem="$signing_key_pem" \
  docker run -d --name "$api" --label "$label" --network "$network" -p "127.0.0.1::$api_port" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e "AllowedHosts=api.example.test;127.0.0.1;localhost" \
  -e ConnectionStrings__Database \
  -e Auth__Jwt__SigningKeys__0__KeyId=verify \
  -e Auth__Jwt__SigningKeys__0__PrivateKeyPem \
  "$image" >/dev/null
pass "the API container started"

step "4/7 Waiting for the HEALTHCHECK to report healthy"
deadline=$((SECONDS + timeout))
while :; do
  health="$(inspect "$api" '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}')"
  case "$health" in
    healthy) break ;;
    none) fail "the image declares no HEALTHCHECK." ;;
    unhealthy) fail "the HEALTHCHECK reports unhealthy." ;;
  esac
  api_state="$(inspect "$api" '{{.State.Status}}')"
  if [ "$api_state" != running ]; then
    fail "the API container is $api_state, not running."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "the HEALTHCHECK did not report healthy within ${timeout}s (last status: $health)."
  fi
  sleep 2
done
pass "the HEALTHCHECK reports healthy"

step "5/7 GET /health/ready from this machine"
published="$(docker port "$api" "$api_port/tcp")"
published="${published%%$'\n'*}"
host_port="${published##*:}"
if ! [[ "$host_port" =~ ^[0-9]+$ ]]; then
  fail "could not read the published port of $api_port/tcp (docker port printed '$published')."
fi
ready_url="http://127.0.0.1:$host_port/health/ready"
deadline=$((SECONDS + timeout))
while :; do
  ready_status="$(curl -sS -o "$work/ready.body" -w '%{http_code}' --max-time 10 "$ready_url" 2>"$work/curl.err")" || ready_status="000"
  if [ "$ready_status" = 200 ]; then
    break
  fi
  api_state="$(inspect "$api" '{{.State.Status}}')"
  if [ "$api_state" != running ]; then
    fail "the API container is $api_state, not running."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "$ready_url answered $ready_status, not 200, for ${timeout}s ($(cat "$work/curl.err"))."
  fi
  sleep 2
done
pass "$ready_url answered 200"

step "6/7 Checking the container's user"
user="$(inspect "$api" '{{.Config.User}}')"
case "$user" in
  "" | root | 0 | root:* | 0:* | missing) fail "the container runs as root (configured user: '${user}')." ;;
esac
pass "the container runs as user '$user', not root"

step "7/7 Removing the containers and the network"
# The EXIT trap removes them too (a no-op by then); here a passing run also proves nothing is left behind.
remove_created
leftover_containers="$(docker ps -aq --filter "label=$label")"
leftover_networks="$(docker network ls -q --filter "label=$label")"
if [ -n "$leftover_containers$leftover_networks" ]; then
  fail "containers or networks labelled $label are still there after removal."
fi
pass "no container or network labelled $label is left"

echo
echo "container-verify: every check passed for $image"
