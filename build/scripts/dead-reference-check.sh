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
  # No allowlist: the tracing sampler (a "Sample = ..." property) matches none of these patterns.
  scan 'Modules\.Sample|SampleModule|SampleDbContext|SampleError|modules/sample|Sample module|LeaveRequest|leave request' '--includeSample false'
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
