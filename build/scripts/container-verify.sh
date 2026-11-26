#!/usr/bin/env bash
# Verifies an API image, the one set of checks CI (ci.yml, job `container`) and CD (cd.yml, on the pushed digest) both run:
#   1. start a throwaway SQL Server on a private Docker network, with a password generated for this run;
#   2. run `<image> migrate` against it, retried while the server starts, then run it again: both must exit 0;
#   3. start `<image>` as the API in Production with the minimum configuration listed below and a non-wildcard AllowedHosts;
#   4. wait until Docker reports the image's own HEALTHCHECK as `healthy`;
#   5. GET /health/ready from this machine: 200;
#   6. the container's configured user is not root;
#   7. start `<image>` in Production without the key ring certificate: it must exit non-zero and name Auth:DataProtection:Certificate;
#   8. remove every container and the network, whatever the outcome; on failure print the container logs first.
#
# Usage: bash build/scripts/container-verify.sh <image>      for example api:ci, or ghcr.io/owner/repo@sha256:...
#
# Environment:
#   VERIFY_TIMEOUT    seconds each wait may take (SQL Server ready, the migrate retries, healthy, ready); default 120. One migrate
#                     attempt is bounded by what is left of its wait (a hung attempt is killed and counts as a failed one).
#   VERIFY_SQL_IMAGE  the SQL Server image; default mcr.microsoft.com/mssql/server:2022-latest, as docker-compose.yml and the tests.
# Needs docker, openssl, curl and timeout (GNU coreutils; on macOS Homebrew installs it as `gtimeout`: put coreutils' gnubin on
# PATH or alias it). Exits 0 only when every check passes.
#
# Clean-up: every container and the network it creates carry the label container-verify=<pid of this script>-<random hex> (so two
# runs on one daemon never remove each other's objects) and are removed on exit, normal or not (EXIT, INT, TERM; a second signal
# during the clean-up is ignored, so the clean-up always finishes), so after any run that has finished both of these print nothing:
#   docker ps -a --filter label=container-verify -q
#   docker network ls --filter label=container-verify -q
# A SIGKILL cannot be trapped; remove what it leaves with the same label filters.
#
# Secrets: the SA password (`openssl rand -hex 12` plus `Aa1!`, for SQL Server's complexity rule), the ES256 signing key and the
# password of the key ring certificate's PFX are made here for this run only. None of the three is written to a file (the PFX itself
# is, in $work, which is removed on exit, but its password is not). When GITHUB_ACTIONS is set each is masked before any use. They
# reach a container by name (`-e NAME`, taken from the environment of that one docker command), never on a command line, so the
# runner's process list does not show them.
#
# The minimum Production configuration (docs/deployment/docker.md, "Running it"; ADR 0033). Production refuses to start without:
#   ConnectionStrings__Database                    ConnectionStringsOptions.Database is [Required], validated on start; `migrate`
#                                                  reads it too (ADR 0006), and is given nothing else.
#   Auth__Jwt__SigningKeys__0__KeyId               JwtOptions: an empty Auth:Jwt:SigningKeys fails ValidateOnStart outside Development and
#   Auth__Jwt__SigningKeys__0__PrivateKeyPem       Testing; the key must be EC P-256, PEM 'PRIVATE KEY' (PKCS#8) or 'EC PRIVATE KEY'.
#   Auth__DataProtection__Certificate__Source      KeyRingStartupCheck: outside Development and Testing the host does not start unless the
#   Auth__DataProtection__Certificate__Path        Data Protection key ring is wrapped with an RSA certificate that is valid now and holds
#   Auth__DataProtection__Certificate__Password    no plaintext key. Here: a throwaway PFX made for this run (RSA, self-signed), mounted
#                                                  read-only at /certs/dp.pfx, with Source File. `migrate` is given none of these: it
#                                                  never starts the host, so it needs no certificate.
# ASPNETCORE_ENVIRONMENT=Production is set explicitly (the image's default is Production too).
# AllowedHosts is set the way the docs tell a deployment to set it, not left at `*`: a public host name plus 127.0.0.1 (the
# image's HEALTHCHECK sends Host 127.0.0.1:<port>) and localhost. Host filtering answers 400 to any other Host, so steps 4 and 5
# (the HEALTHCHECK and the curl to 127.0.0.1) prove that this documented shape keeps the probes working.
# Everything else starts in Production with the defaults of appsettings.json, read from the options classes and the module
# registrations (AddInfrastructureCommon, AddHttpSecurity, AddAuthModule, AddAuditModule):
#if (notifications)
#   - AddNotificationsModule reads the Notifications section the same way;
#endif
#if (caching == "redis")
#   - AddPlatformRedis registers nothing while Caching:Provider is memory (the default, below);
#endif
#   - the only environment-dependent checks in src/ are the signing key (above), HSTS plus HTTPS redirection outside Development
#     (with no https port configured, UseHttpsRedirection only logs a warning and passes the request on; /health/* is exempt from the
#     redirect anyway; HSTS acts on https requests only), OpenAPI and Scalar (not mapped in Production) and the log format;
#   - Auth:Links: the localhost defaults pass (absolute http(s) with {token}); a real deployment sets its front-end URLs;
#if (notifications)
#   - Notifications:Email: host localhost and no username pass; SMTP is reached only for a pending email delivery, none here;
#     no key may exist under Auth:Email (LegacyEmailSettingsCheck);
#endif
#   - Cors (empty list), ForwardedHeaders (empty lists), RateLimiting, Outbox, Idempotency, Retention, Jobs, Caching (memory, so no
#     Redis), Localization, Auth:Password/Verification/RefreshToken/Lockout/Mfa/StepUp/Retention, Auth:External (every provider
#     disabled), Audit: valid defaults;
#if (notifications)
#   - Notifications:Templates/Delivery/Hub/Retention: valid defaults;
#endif
#   - Data Protection: the key ring is kept in auth.DataProtectionKeys (made by `migrate`), not on the container's file system, and
#     wrapped with the certificate above (step 7 proves the host refuses to start without it);
#   - Auth:Seed:RunOnStartup (on by default) seeds roles and permissions before the host starts, so the database must be migrated
#     first (step 2); with Auth:Seed:AdminEmail/AdminPassword empty no administrator is created;
#   - OTEL_EXPORTER_OTLP_ENDPOINT unset: no exporter.
# The integration test HostTests.Scalar_is_not_mapped_in_production starts Production with the same files plus a signing key and a
# throwaway key ring certificate.
# Not ruled out without running the image: that test switches the outbox dispatcher, the recurring jobs and the other background
# services off and skips the seeding, while the container runs all of them; a background service that throws stops the host (and fails
# step 4 here, with the logs printed).
set -euo pipefail

readonly sql_image="${VERIFY_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
readonly wait_limit="${VERIFY_TIMEOUT:-120}"
readonly api_port=8080
# Set once the tools are known to exist (the random part needs openssl); nothing that uses them runs before $work is set.
label=""
prefix=""
network=""
sql=""
api=""
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
  echo >&2
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
  # Ignore INT and TERM from here on: GitHub sends SIGINT and, about 7.5 s later, SIGTERM when it cancels a run, and a second
  # signal must not cut the clean-up short (a trap that is reset would let it kill the script before the removal ran).
  trap - EXIT
  trap '' INT TERM
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

if ! [[ "$wait_limit" =~ ^[1-9][0-9]*$ ]]; then
  fail "VERIFY_TIMEOUT must be a whole number of seconds, greater than 0."
fi
for tool in docker openssl curl timeout; do
  command -v "$tool" >/dev/null 2>&1 || fail "$tool is required."
done

# Unique per run, not only per process: another run on the same daemon (a self-hosted runner, a re-run, a PID reused in another
# container) must neither share these names nor have its objects matched by this run's clean-up filter.
run_id="$$-$(openssl rand -hex 3)"
label="container-verify=$run_id"
prefix="container-verify-$run_id"
network="$prefix"
sql="$prefix-sql"
api="$prefix-api"
readonly run_id label prefix network sql api

work="$(mktemp -d)"

# The secrets of this run, masked before any use.
sa_password="$(openssl rand -hex 12)Aa1!"
signing_key_pem="$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)"
dp_password="$(openssl rand -hex 16)"
if [ -n "${GITHUB_ACTIONS:-}" ]; then
  echo "::add-mask::$sa_password"
  echo "::add-mask::$dp_password"
  while IFS= read -r line; do
    case "$line" in
      "" | -----*) ;;
      *) echo "::add-mask::$line" ;;
    esac
  done <<<"$signing_key_pem"
fi
connection_string="Server=$sql,1433;Database=Verify;User Id=sa;Password=$sa_password;Encrypt=True;TrustServerCertificate=True"

# The key ring certificate: an RSA key and a self-signed certificate in a PFX, valid from now. The file is readable by any user (the
# image runs as a non-root user that is not the owner of $work) and mounted read-only; the key never leaves $work.
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/dp.key" -out "$work/dp.crt" -days 3 -subj "/CN=container-verify" 2>/dev/null
DP_PFX_PASSWORD="$dp_password" openssl pkcs12 -export -inkey "$work/dp.key" -in "$work/dp.crt" -out "$work/dp.pfx" -passout env:DP_PFX_PASSWORD
chmod 644 "$work/dp.pfx"

echo "Verifying $image (each wait up to ${wait_limit}s; label $label)"

step "1/8 Starting SQL Server ($sql_image) on the private network $network"
docker network create --label "$label" "$network" >/dev/null
MSSQL_SA_PASSWORD="$sa_password" docker run -d --name "$sql" --label "$label" --network "$network" \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD \
  "$sql_image" >/dev/null
deadline=$((SECONDS + wait_limit))
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
    fail "SQL Server was not ready after ${wait_limit}s."
  fi
  sleep 2
done
pass "SQL Server is ready"

# The migrate mode exits 1 on any failure, an unreachable server or a login refused while SQL Server finishes starting included,
# so 1 is retried until the timeout; any other code (125 to 127: Docker could not run the image or the command) fails at once.
# One attempt, bounded: EF's connection retry can keep a single attempt running for over a minute, which would let the wait
# overrun VERIFY_TIMEOUT by a whole attempt. `timeout` stops it (SIGTERM, which docker forwards to the container, then SIGKILL
# after 5 s) and answers 124, or 137 when it had to kill. Both are mapped to 1, a failed attempt; the labelled container, if one
# is left, is removed with the rest at the end. --foreground keeps `timeout` in this script's process group, so a Ctrl-C reaches
# it. On the 137 path the docker CLI is killed but the detached container may keep running until the label clean-up removes it
# (the EF migration lock makes an overlap with the next attempt safe). 137 is also what a container killed by the kernel (out of
# memory) exits with, so the message below covers both. Argument: the seconds the attempt may take.
run_migrate() {
  local status=0
  ConnectionStrings__Database="$connection_string" timeout --foreground --kill-after=5 "$1" \
    docker run --rm --label "$label" --network "$network" \
    -e ASPNETCORE_ENVIRONMENT=Production -e ConnectionStrings__Database \
    "$image" migrate >"$work/migrate.log" 2>&1 || status=$?
  if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then
    echo "migrate exited $status: timed out (limit ${1}s) or killed." >>"$work/migrate.log"
    status=1
  fi
  return "$status"
}

# Seconds left until $deadline, at least 1, for run_migrate.
attempt_seconds() {
  local left=$((deadline - SECONDS))
  if [ "$left" -lt 1 ]; then
    left=1
  fi
  echo "$left"
}

step "2/8 Running '$image migrate', twice"
deadline=$((SECONDS + wait_limit))
attempt=0
while :; do
  attempt=$((attempt + 1))
  if run_migrate "$(attempt_seconds)"; then
    break
  else
    migrate_status=$?
  fi
  if [ "$migrate_status" -ne 1 ]; then
    fail "migrate exited $migrate_status on attempt $attempt; only 1 (the migrate mode's failure) is retried."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "migrate still failed after ${wait_limit}s ($attempt attempts)."
  fi
  sleep 3
done
pass "migrate exited 0 (attempt $attempt)"
deadline=$((SECONDS + wait_limit))
if run_migrate "$(attempt_seconds)"; then
  pass "migrate exited 0 again (idempotent)"
else
  fail "the second migrate exited $? instead of 0."
fi

step "3/8 Starting '$image' as the API in Production"
ConnectionStrings__Database="$connection_string" Auth__Jwt__SigningKeys__0__PrivateKeyPem="$signing_key_pem" \
  Auth__DataProtection__Certificate__Password="$dp_password" \
  docker run -d --name "$api" --label "$label" --network "$network" -p "127.0.0.1::$api_port" \
  -v "$work/dp.pfx:/certs/dp.pfx:ro" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e "AllowedHosts=api.example.test;127.0.0.1;localhost" \
  -e ConnectionStrings__Database \
  -e Auth__Jwt__SigningKeys__0__KeyId=verify \
  -e Auth__Jwt__SigningKeys__0__PrivateKeyPem \
  -e Auth__DataProtection__Certificate__Source=File \
  -e Auth__DataProtection__Certificate__Path=/certs/dp.pfx \
  -e Auth__DataProtection__Certificate__Password \
  "$image" >/dev/null
pass "the API container started"

step "4/8 Waiting for the HEALTHCHECK to report healthy"
deadline=$((SECONDS + wait_limit))
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
    fail "the HEALTHCHECK did not report healthy within ${wait_limit}s (last status: $health)."
  fi
  sleep 2
done
pass "the HEALTHCHECK reports healthy"

step "5/8 GET /health/ready from this machine"
published="$(docker port "$api" "$api_port/tcp")"
published="${published%%$'\n'*}"
host_port="${published##*:}"
if ! [[ "$host_port" =~ ^[0-9]+$ ]]; then
  fail "could not read the published port of $api_port/tcp (docker port printed '$published')."
fi
ready_url="http://127.0.0.1:$host_port/health/ready"
deadline=$((SECONDS + wait_limit))
while :; do
  ready_status="$(curl -sS --noproxy '*' -o "$work/ready.body" -w '%{http_code}' --max-time 10 "$ready_url" 2>"$work/curl.err")" || ready_status="000"
  if [ "$ready_status" = 200 ]; then
    break
  fi
  api_state="$(inspect "$api" '{{.State.Status}}')"
  if [ "$api_state" != running ]; then
    fail "the API container is $api_state, not running."
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "$ready_url answered $ready_status, not 200, for ${wait_limit}s ($(cat "$work/curl.err"))."
  fi
  sleep 2
done
pass "$ready_url answered 200"

step "6/8 Checking the container's user"
user="$(inspect "$api" '{{.Config.User}}')"
case "$user" in
  "" | root | 0 | root:* | 0:* | missing) fail "the container runs as root (configured user: '${user}')." ;;
esac
pass "the container runs as user '$user', not root"

step "7/8 Starting '$image' in Production without the key ring certificate"
# The same configuration as step 3 minus the three Auth__DataProtection__Certificate__* settings: the host must refuse to start, exit
# non-zero and name the missing setting. Run in the foreground and bounded, so a host that wrongly starts is killed, not waited for.
nocert_status=0
ConnectionStrings__Database="$connection_string" Auth__Jwt__SigningKeys__0__PrivateKeyPem="$signing_key_pem" \
  timeout --foreground --kill-after=5 "$wait_limit" \
  docker run --rm --label "$label" --network "$network" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e "AllowedHosts=api.example.test;127.0.0.1;localhost" \
  -e ConnectionStrings__Database \
  -e Auth__Jwt__SigningKeys__0__KeyId=verify \
  -e Auth__Jwt__SigningKeys__0__PrivateKeyPem \
  "$image" >"$work/nocert.log" 2>&1 || nocert_status=$?
case "$nocert_status" in
  0) fail "the API started in Production without a key ring certificate (exit 0); it must refuse." ;;
  124 | 137) fail "the API did not stop within ${wait_limit}s in Production without a key ring certificate; it must refuse to start." ;;
esac
if ! grep -q "Auth:DataProtection:Certificate" "$work/nocert.log"; then
  echo "--- output of the run without the certificate" >&2
  cat "$work/nocert.log" >&2
  fail "the API exited $nocert_status without a key ring certificate but its output does not name Auth:DataProtection:Certificate."
fi
pass "without the certificate the API exits $nocert_status and names Auth:DataProtection:Certificate"

step "8/8 Removing the containers and the network"
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
