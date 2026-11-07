#!/usr/bin/env bash
# Fails when a generated project still names a part that a template switch removed (template switches design).
# Run it on the output of "dotnet new" with the same switch values that produced the tree; it checks only the parts that were removed.
#
# Usage: bash build/scripts/dead-reference-check.sh <generated-dir> <notifications: true|false> <includeSample: true|false> <caching: redis|memory>
# Output: one "path:line: text" per hit on stdout, paths relative to <generated-dir>.
# Exit: 0 clean, 1 at least one hit, 2 usage error.
#
# Never searched: bin/, obj/, .git/, packages.lock.json (generated files) and docs/adr/ (a decision record keeps the history of what the
# template once contained). A line that quotes the switch itself ("--notifications false") is the generated README saying what was left out.
set -euo pipefail

fail() { echo "dead-reference-check: $*" >&2; exit 2; }

(( $# == 4 )) || fail "usage: dead-reference-check.sh <generated-dir> <notifications: true|false> <includeSample: true|false> <caching: redis|memory>"
dir="$1"
notifications="$2"
include_sample="$3"
caching="$4"

[[ -d "$dir" ]] || fail "not a directory: $dir"
case "$notifications" in true | false) ;; *) fail "notifications must be 'true' or 'false', got '$notifications'" ;; esac
case "$include_sample" in true | false) ;; *) fail "includeSample must be 'true' or 'false', got '$include_sample'" ;; esac
case "$caching" in redis | memory) ;; *) fail "caching must be 'redis' or 'memory', got '$caching'" ;; esac

cd "$dir"
hits=0

# scan <pattern> <flag-text> [allowed-path-regex...]: reports every line of the tree matching the ERE pattern, except in the paths and
# lines the rules above allow. The allowed-path regexes are matched against the path relative to the tree.
scan() {
  local pattern="$1" flag_text="$2"
  shift 2
  local line path rest lineno text allowed rule
  while IFS= read -r line; do
    line="${line#./}"
    path="${line%%:*}"
    rest="${line#*:}"
    lineno="${rest%%:*}"
    text="${rest#*:}"

    # A decision record keeps the history of what the template once contained.
    [[ "$path" == docs/adr/* ]] && continue
    # The generated README names the switch that removed the part.
    [[ "$text" == *"$flag_text"* ]] && continue

    allowed=0
    for rule in "$@"; do
      if [[ "$path" =~ $rule ]]; then allowed=1; break; fi
    done
    (( allowed == 1 )) && continue

    printf '%s:%s: %s\n' "$path" "$lineno" "$text"
    hits=$((hits + 1))
  done < <(grep -rnE -I --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude=packages.lock.json -e "$pattern" . || true)
}

if [[ "$notifications" == "false" ]]; then
  allow=(
    # BatchedCleanupJob's doc comment only uses NotificationsDbContext as an example of a context name.
    '^src/BuildingBlocks/[^/]+\.Infrastructure\.Common/Jobs/BatchedCleanupJob\.cs$'
  )
  scan 'Notifications|Mailpit|mailpit|\bnotify\b' '--notifications false' "${allow[@]}"
fi

if [[ "$include_sample" == "false" ]]; then
  # The tracing sampler (a "Sample = ..." property) matches none of these patterns.
  # The module's names: allowed only where the naming conventions and the building-block pages use them as examples of a name.
  allow=(
    # The naming tables give Sample's names as the example of each rule (SampleDbContext, SampleModule, the namespace, the paths).
    '^docs/coding-conventions\.md$'
  )
  scan 'modules/sample|Sample module' '--includeSample false' "${allow[@]}"
  allow+=(
    # The code examples of a handler's registration and of a module DbContext are written as Sample's.
    '^docs/building-blocks/(application|infrastructure)-common\.md$'
  )
  scan 'Modules\.Sample|SampleModule|SampleDbContext|SampleError' '--includeSample false' "${allow[@]}"
  # Its aggregate and its schema: also allowed where "LeaveRequest" or "sample" is only an example name or a string of test data,
  # never a type, a table or a permission the code uses.
  allow+=(
    # The code examples of an aggregate and an endpoint group use the leave request.
    '^docs/building-blocks/(shared-kernel|web-common)\.md$'
    # The Conventional Commit examples.
    '^CONTRIBUTING\.md$'
    # Doc comments that give "LeaveRequest" as an example of an entity type name.
    '^src/BuildingBlocks/[^/]+\.SharedKernel/Auditing/EntityChange\.cs$'
    '^src/Modules/Audit/[^/]+/Application/Trails/List/AuditTrailResponse\.cs$'
    # Tests of the audit trail and the permission synchronizer whose data names an entity type or a permission as plain strings.
    '^tests/[^/]+\.UnitTests/(Audit/AuditTrailTests|Auth/PermissionSynchronizerTests)\.cs$'
    '^tests/[^/]+\.UnitTests/(SharedKernel|Infrastructure)/Auditing/EntityChangesRecorded(Event|DomainEventHandler)Tests\.cs$'
    '^tests/[^/]+\.IntegrationTests/Audit/Audit(Consumer|Persistence|TrailEndpoint|TrailsCleanupJob)Tests\.cs$'
  )
  # "sample" and 'sample' as a string, sample.Table, sample.permission_code.
  scan 'LeaveRequest|leave request|"sample"|'"'sample'"'|\bsample\.[A-Za-z_]' '--includeSample false' "${allow[@]}"
fi

if [[ "$caching" == "memory" ]]; then
  allow=(
    # The Caching folder's provider switch still names the redis provider so that it can reject "redis" without a distributed cache.
    '^src/BuildingBlocks/[^/]+\.Infrastructure\.Common/Caching/'
    # The Security folder's comments say AddPlatformRedis replaces the in-memory rate limiter.
    '^src/BuildingBlocks/[^/]+\.Web\.Common/Security/'
    # The test that proves a project without the Redis project refuses Caching:Provider=redis names that project and AddPlatformRedis.
    '^tests/[^/]+\.IntegrationTests/Caching/CachingProviderWithoutRedisTests\.cs$'
  )
  scan 'Platform\.Redis|PlatformRedis|platform-redis|StackExchange|[Vv]alkey' '--caching memory' "${allow[@]}"
fi

if (( hits > 0 )); then
  echo "dead-reference-check: $hits dead reference(s) to a part that a switch removed" >&2
  exit 1
fi
