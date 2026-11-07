#!/usr/bin/env bash
# Tests for dead-reference-check.sh (template switches design): a generated tree must not name a part that a switch removed.
# Each case builds its own throwaway tree, runs the script on it and checks stdout and the exit code.
#
# Usage: bash build/scripts/tests/dead-reference-check.test.sh
set -euo pipefail

scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
check="$scripts_dir/dead-reference-check.sh"

failures=0
# Every case tree lives under one root, created here so that the trap (which subshells do not inherit) removes it.
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

# must <command...>: runs a setup command and ends the case (it runs in a subshell) when the command fails. A case runs on the
# left of ||, where bash ignores set -e, so a broken setup would otherwise carry on and let the negative cases pass vacuously.
must() {
  "$@" || { echo "FAIL: setup command failed: $*" >&2; exit 1; }
}

# make_tree: creates an empty generated tree and sets tree to its path.
make_tree() {
  tree="$(mktemp -d "$tmp_root/tree.XXXXXX")" || exit 1
}

# put <relative path> <content>: writes a file into the current tree.
put() {
  must mkdir -p "$tree/$(dirname "$1")"
  printf '%s\n' "$2" > "$tree/$1" || exit 1
}

assert_eq() {
  local expected="$1" actual="$2" name="$3"
  if [[ "$expected" == "$actual" ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected '$expected', got '$actual'" >&2
    failures=$((failures + 1))
  fi
}

assert_contains() {
  local needle="$1" haystack="$2" name="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected to contain '$needle', got '$haystack'" >&2
    failures=$((failures + 1))
  fi
}

# run_check <notifications> <includeSample> <caching>: runs the script on the current tree; sets out, err and status.
run_check() {
  local errfile
  errfile="$(mktemp)"
  status=0
  out="$(bash "$check" "$tree" "$1" "$2" "$3" 2> "$errfile")" || status=$?
  err="$(cat "$errfile")"
  rm -f "$errfile"
}

# Every pattern of every part, one per line.
everything() {
  printf '%s\n' 'Modules.Notifications' 'Mailpit' 'mailpit' 'notify the user' 'notifications.delivery.view' '/api/v1/notifications' \
    'notifications-hub-tickets' 'Factory.EmailSender' 'the delivery worker' \
    'Modules.Sample' 'SampleModule' 'SampleDbContext' 'SampleError' 'modules/sample' 'Sample module' 'LeaveRequest' 'leave request' \
    'Platform.Redis' 'PlatformRedis' 'platform-redis' 'StackExchange.Redis' 'Valkey' 'valkey'
}

case_all_switches_on() {
  make_tree
  put src/A.cs "$(everything)"
  put docs/x.md "$(everything)"
  run_check true true redis
  assert_eq "0" "$status" "all_switches_on: exit code"
  assert_eq "" "$out" "all_switches_on: stdout empty"
}

case_notifications_removed_hit() {
  make_tree
  put src/A.cs 'Modules.Notifications'
  run_check false true redis
  assert_eq "1" "$status" "notifications_removed_hit: exit code"
  assert_contains "src/A.cs:1" "$out" "notifications_removed_hit: names file and line"
}

case_notifications_other_patterns() {
  local p
  for p in 'Mailpit' 'mailpit' 'we notify the user'; do
    make_tree
    put src/A.cs "$p"
    run_check false true redis
    assert_eq "1" "$status" "notifications_other_patterns: '$p'"
  done
  # notify is matched as a word only.
  make_tree
  put src/A.cs 'the notifyAll helper'
  run_check false true redis
  assert_eq "0" "$status" "notifications_other_patterns: notifyAll is not the word"
}

case_hit_line_number() {
  make_tree
  put src/A.cs "$(printf 'one\ntwo\nMailpit here')"
  run_check false true redis
  assert_eq "1" "$status" "hit_line_number: exit code"
  assert_contains "src/A.cs:3:" "$out" "hit_line_number: names line 3"
  assert_contains "Mailpit here" "$out" "hit_line_number: shows the text"
}

case_ignored_paths() {
  local p
  for p in docs/adr/0020-x.md obj/x.cs bin/x.dll.txt src/packages.lock.json src/Deep/obj/y.cs src/Deep/bin/y.txt; do
    make_tree
    put "$p" 'Modules.Notifications'
    run_check false true redis
    assert_eq "0" "$status" "ignored_paths: $p"
  done
}

case_flag_text_line_allowed() {
  make_tree
  put README.md 'Notifications was left out: --notifications false'
  run_check false true redis
  assert_eq "0" "$status" "flag_text_line_allowed: with the flag text"
  make_tree
  put README.md 'Notifications was left out'
  run_check false true redis
  assert_eq "1" "$status" "flag_text_line_allowed: without the flag text"
  assert_contains "README.md:1" "$out" "flag_text_line_allowed: names file and line"
}

case_redis_allowlist() {
  make_tree
  put src/BuildingBlocks/Acme.Infrastructure.Common/Caching/X.cs 'PlatformRedis'
  run_check true true memory
  assert_eq "0" "$status" "redis_allowlist: Caching folder"
  make_tree
  put src/BuildingBlocks/Acme.Web.Common/Security/X.cs 'StackExchange'
  run_check true true memory
  assert_eq "0" "$status" "redis_allowlist: Security folder"
  make_tree
  put src/Host/Acme.Api/Program.cs 'PlatformRedis'
  run_check true true memory
  assert_eq "1" "$status" "redis_allowlist: outside the allowlist"
  assert_contains "src/Host/Acme.Api/Program.cs:1" "$out" "redis_allowlist: names file and line"
  make_tree
  put tests/Acme.IntegrationTests/Caching/CachingProviderWithoutRedisTests.cs 'AddPlatformRedis'
  run_check true true memory
  assert_eq "0" "$status" "redis_allowlist: the test of a project without the Redis project"
  make_tree
  put tests/Acme.IntegrationTests/Caching/CachingProviderTests.cs 'AddPlatformRedis'
  run_check true true memory
  assert_eq "1" "$status" "redis_allowlist: another test in the Caching folder"
}

case_redis_other_patterns() {
  local p
  for p in 'Platform.Redis' 'platform-redis' 'StackExchange.Redis' 'Valkey' 'valkey'; do
    make_tree
    put src/Host/Acme.Api/Program.cs "$p"
    run_check true true memory
    assert_eq "1" "$status" "redis_other_patterns: '$p'"
  done
}

case_notifications_allowlist() {
  make_tree
  put src/BuildingBlocks/Acme.Infrastructure.Common/Jobs/BatchedCleanupJob.cs 'Notifications'
  run_check false true redis
  assert_eq "0" "$status" "notifications_allowlist: BatchedCleanupJob"
  make_tree
  put src/BuildingBlocks/Acme.Infrastructure.Common/Jobs/OtherJob.cs 'Notifications'
  run_check false true redis
  assert_eq "1" "$status" "notifications_allowlist: another job"
  make_tree
  put tests/Acme.UnitTests/Infrastructure/OutboxAbandonedGaugeTests.cs '_sut.Set("Notifications", 2, job);'
  put tests/Acme.UnitTests/Web/AccessTokenRedactionTests.cs '[InlineData("/hubs/notifications")]'
  run_check false true redis
  assert_eq "0" "$status" "notifications_allowlist: building-block tests with the module as test data"
  make_tree
  put tests/Acme.UnitTests/Web/OtherTests.cs '[InlineData("/hubs/notifications")]'
  run_check false true redis
  assert_eq "1" "$status" "notifications_allowlist: another web test"
}

case_notifications_kept_names() {
  local p
  for p in 'await DispatchNotificationsAsync();' 'protected Task DispatchNotificationsAsync() => DispatchNotificationsAsync(Factory.Services);' \
    'throw new InvalidOperationException($"Notifications were still being dispatched after {MaxRounds} rounds.");'; do
    make_tree
    put tests/Acme.IntegrationTests/Auth/X.cs "$p"
    run_check false true redis
    assert_eq "0" "$status" "notifications_kept_names: '$p'"
  done
  local q
  for q in 'await DispatchNotificationsAsync(); // then read the Notifications inbox' 'DispatchNotifications(); NotificationsDbContext' \
    'await DispatchNotificationsAsync(); Mailpit'; do
    make_tree
    put tests/Acme.IntegrationTests/Auth/X.cs "$q"
    run_check false true redis
    assert_eq "1" "$status" "notifications_kept_names: '$q' names the module too"
  done
}

case_notifications_schema() {
  local p
  for p in 'schemas.ShouldContain("notify");' '{ "notify.OutboxMessages", "ProcessedAt", true },' "WHERE TABLE_SCHEMA = 'notify'" \
    'await SignInAsync("notifications.delivery.view");' 'await client.GetAsync("/api/v1/notifications/inbox", Ct);' \
    '"notifications-hub-tickets"' 'see docs/modules/notifications.md'; do
    make_tree
    put tests/Acme.IntegrationTests/Host/X.cs "$p"
    run_check false true redis
    assert_eq "1" "$status" "notifications_schema: '$p'"
  done
  local q
  for q in 'the notifications scope of a commit' 'send notifications to users' 'docs/modules/auth.md'; do
    make_tree
    put tests/Acme.IntegrationTests/Host/X.cs "$q"
    run_check false true redis
    assert_eq "0" "$status" "notifications_schema: '$q' is not the module"
  done
}

case_notifications_email_transport_and_worker() {
  local p
  for p in 'Factory.EmailSender.Sent.ShouldBeEmpty();' '`RecordingEmailSender` keeps the messages' 'the delivery worker sends the emails'; do
    make_tree
    put docs/x.md "$p"
    run_check false true redis
    assert_eq "1" "$status" "notifications_email_transport_and_worker: '$p'"
  done
  local q
  for q in 'a delivery of the same event' 'the sender of an email' 'a background worker'; do
    make_tree
    put docs/x.md "$q"
    run_check false true redis
    assert_eq "0" "$status" "notifications_email_transport_and_worker: '$q' is not the module"
  done
}

case_sample_removed() {
  make_tree
  put tests/T.cs 'LeaveRequest'
  run_check true false redis
  assert_eq "1" "$status" "sample_removed: LeaveRequest"
  assert_contains "tests/T.cs:1" "$out" "sample_removed: names file and line"
  make_tree
  put src/Tracing.cs 'Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>'
  run_check true false redis
  assert_eq "0" "$status" "sample_removed: the tracing sampler is not the module"
}

case_sample_other_patterns() {
  local p
  for p in 'Modules.Sample' 'SampleModule' 'SampleDbContext' 'SampleError' 'modules/sample' 'Sample module' 'leave request'; do
    make_tree
    put src/A.cs "$p"
    run_check true false redis
    assert_eq "1" "$status" "sample_other_patterns: '$p'"
  done
}

case_sample_allowlist() {
  make_tree
  put docs/coding-conventions.md "$(printf 'SampleDbContext\nLeaveRequest')"
  run_check true false redis
  assert_eq "0" "$status" "sample_allowlist: the naming conventions"
  make_tree
  put docs/building-blocks/shared-kernel.md 'LeaveRequest'
  run_check true false redis
  assert_eq "0" "$status" "sample_allowlist: an example aggregate"
  make_tree
  put docs/building-blocks/shared-kernel.md 'SampleModule'
  run_check true false redis
  assert_eq "1" "$status" "sample_allowlist: the module's name on a page that only borrows its aggregate"
  make_tree
  put tests/Acme.IntegrationTests/Audit/AuditTrailEndpointTests.cs 'LeaveRequest'
  run_check true false redis
  assert_eq "0" "$status" "sample_allowlist: test data of the audit trail"
  make_tree
  put tests/Acme.IntegrationTests/Audit/AuditTrailEndpointTests.cs 'using Acme.Modules.Sample.Domain;'
  run_check true false redis
  assert_eq "1" "$status" "sample_allowlist: the module's namespace in a test of the audit trail"
  make_tree
  put tests/Acme.IntegrationTests/Audit/AuditTrailEndToEndTests.cs 'LeaveRequest'
  run_check true false redis
  assert_eq "1" "$status" "sample_allowlist: another audit test"
  make_tree
  put docs/building-blocks/application-common.md "$(printf 'services.AddApplicationHandlers(typeof(SampleModule).Assembly);\n// schema "sample"')"
  run_check true false redis
  assert_eq "0" "$status" "sample_allowlist: a building-block code example"
  make_tree
  put docs/building-blocks/application-common.md 'the Sample module is the worked example'
  run_check true false redis
  assert_eq "1" "$status" "sample_allowlist: a building-block page saying the module exists"
  make_tree
  put tests/Acme.IntegrationTests/Audit/AuditTrailEndpointTests.cs "$(printf 'string module = "sample",\nVALUES (NEWID(), '"'sample'"')')"
  run_check true false redis
  assert_eq "0" "$status" "sample_allowlist: the schema as test data of the audit trail"
}

case_sample_schema() {
  local p
  for p in 'schemas.ShouldContain("sample");' '{ "sample.OutboxMessages", "ProcessedAt", true },' "WHERE TABLE_SCHEMA = 'sample'" \
    'await SignInAsync("sample.leave_request.create");'; do
    make_tree
    put tests/Acme.IntegrationTests/Host/X.cs "$p"
    run_check true false redis
    assert_eq "1" "$status" "sample_schema: '$p'"
  done
  local q
  for q in 'a sample of the rows' 'Sample.verified.json' 'the sampler'; do
    make_tree
    put tests/Acme.IntegrationTests/Host/X.cs "$q"
    run_check true false redis
    assert_eq "0" "$status" "sample_schema: '$q' is not the schema"
  done
}

case_each_part_checked_only_when_removed() {
  make_tree
  put src/A.cs "$(everything)"
  run_check true true redis
  assert_eq "0" "$status" "only_when_removed: nothing removed"
  run_check false true redis
  assert_eq "1" "$status" "only_when_removed: notifications removed"
  assert_contains "src/A.cs:2" "$out" "only_when_removed: notifications line (Mailpit)"
  run_check true false memory
  assert_eq "1" "$status" "only_when_removed: sample and redis removed"
}

case_caching_redis_never_checked() {
  make_tree
  put src/Host/Acme.Api/Program.cs 'Platform.Redis'
  run_check true true redis
  assert_eq "0" "$status" "caching_redis_never_checked: exit code"
}

case_usage_errors() {
  make_tree
  local rc=0
  bash "$check" > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: no arguments"
  rc=0
  bash "$check" "$tree" true true > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: three arguments"
  rc=0
  bash "$check" "$tree" true true redis extra > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: five arguments"
  run_check maybe true redis
  assert_eq "2" "$status" "usage_errors: notifications=maybe"
  run_check true maybe redis
  assert_eq "2" "$status" "usage_errors: includeSample=maybe"
  run_check true true valkey
  assert_eq "2" "$status" "usage_errors: caching=valkey"
  rc=0
  bash "$check" "$tree/missing" true true redis > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: directory missing"
}

# Each case runs in a subshell so that its tree and variables do not leak into the next.
# The subshell reports its failures through its exit code, since variables do not cross the boundary.
run_case() {
  local name="$1" rc=0
  ( failures=0; "$name"; exit $(( failures > 0 ? 1 : 0 )) ) || rc=$?
  if (( rc != 0 )); then failures=$((failures + 1)); fi
}

for c in \
  case_all_switches_on case_notifications_removed_hit case_notifications_other_patterns case_hit_line_number \
  case_ignored_paths case_flag_text_line_allowed case_redis_allowlist case_redis_other_patterns \
  case_notifications_allowlist case_notifications_kept_names case_notifications_schema case_notifications_email_transport_and_worker case_sample_removed case_sample_other_patterns case_sample_allowlist case_sample_schema \
  case_each_part_checked_only_when_removed case_caching_redis_never_checked case_usage_errors; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "dead-reference-check: $failures failure(s)" >&2
  exit 1
fi
echo "dead-reference-check: passed"
