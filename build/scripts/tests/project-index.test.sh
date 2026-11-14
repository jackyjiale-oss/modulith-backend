#!/usr/bin/env bash
# Tests for project-index.sh (project index design): the CLI, the markers, routes, permissions, errors, events, jobs, config and tests
# shards, the MAP.md checks and --check.
# Each case builds its own throwaway tree, runs the script on it and checks the files it writes, its output and its exit code.
#
# Usage: bash build/scripts/tests/project-index.test.sh [case_name...]
#   no argument   run every registered case (the list at the end of this file)
#   case_name...  run only these cases, in this order (for iterating on one case); each must be a registered case name, else the
#                 script runs nothing and exits 2 with "unknown case: <name>"
set -euo pipefail

test_script="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gen="$scripts_dir/project-index.sh"

failures=0
# Every case tree lives under one root, created here so that the trap (which subshells do not inherit) removes it.
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

shards="routes permissions errors events jobs config tests markers"

# must <command...>: runs a setup command and ends the case (it runs in a subshell) when the command fails. A case runs on the
# left of ||, where bash ignores set -e, so a broken setup would otherwise carry on and let the negative cases pass vacuously.
must() {
  "$@" || { echo "FAIL: setup command failed: $*" >&2; exit 1; }
}

# make_tree: creates an empty tree and an empty output directory beside it; sets tree and idx to their paths.
make_tree() {
  local base
  base="$(mktemp -d "$tmp_root/case.XXXXXX")" || exit 1
  tree="$base/tree"
  idx="$base/idx"
  must mkdir -p "$tree"
}

# put <relative path> <content>: writes a file into the current tree.
put() {
  if [[ "$1" == */* ]]; then must mkdir -p "$tree/${1%/*}"; fi
  printf '%s\n' "$2" > "$tree/$1" || exit 1
}

# put_raw <relative path> <printf format>: writes a file with exactly the bytes the format gives (for CRLF line endings).
put_raw() {
  if [[ "$1" == */* ]]; then must mkdir -p "$tree/${1%/*}"; fi
  printf "$2" > "$tree/$1" || exit 1
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

assert_not_contains() {
  local needle="$1" haystack="$2" name="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected not to contain '$needle', got '$haystack'" >&2
    failures=$((failures + 1))
  fi
}

# run_gen <arguments...>: runs the script; sets out (stdout), err (stderr) and status.
run_gen() {
  local errfile
  errfile="$(mktemp "$tmp_root/err.XXXXXX")"
  status=0
  out="$(bash "$gen" "$@" 2> "$errfile")" || status=$?
  err="$(cat "$errfile")"
  rm -f "$errfile"
}

# generate: writes the shards of the current tree into idx.
generate() {
  run_gen --root "$tree" --out "$idx"
  assert_eq "0" "$status" "setup: generate exit code"
}

# check: runs --check on the current tree against idx; sets out, err and status; combined holds stdout and stderr.
check() {
  run_gen --check --root "$tree" --out "$idx"
  combined="$out$err"
}

# write_map <body>: writes MAP.md into idx; without a body, a valid one that names every shard.
write_map() {
  local body="${1-}" s
  must mkdir -p "$idx"
  if [[ -z "$body" ]]; then
    body="# Map"$'\n'
    for s in $shards; do body+="- \`$s.md\`"$'\n'; done
  fi
  printf '%s' "$body" > "$idx/MAP.md" || exit 1
}

# rows <shard>: the table rows (after the header and the separator) of a shard of idx.
rows() {
  local lines=()
  mapfile -t lines < "$idx/$1.md"
  if (( ${#lines[@]} > 8 )); then printf '%s
' "${lines[@]:8}"; fi
}

# make_git_tree: makes the current tree a git repository (the index is enough: nothing is committed).
make_git_tree() {
  must git -C "$tree" init -q
  must git -C "$tree" config core.autocrlf false
}

case_usage_errors() {
  make_tree
  local rc=0
  bash "$gen" --bogus > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: unknown argument"
  rc=0
  bash "$gen" --root > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --root without a value"
  rc=0
  bash "$gen" --root "$tree" --out > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --out without a value"
  rc=0
  bash "$gen" --check --root "$tree/missing" --out "$idx" > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --check with a missing --root directory"
  rc=0
  bash "$gen" --root "$tree/missing" --out "$idx" > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: a missing --root directory in write mode"
  rc=0
  bash "$gen" stray > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: a stray word"
}

case_markers_shard() {
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put b.md "$(printf '<!--#if (includeSample)-->\nsample text\n<!--#endif-->\nWrite `//#if (x)` to switch a part off.')"
  put c.yml "$(printf 'top: 1\n  #if (caching == "redis")\n  redis: x\n  #endif')"
  put d.txt 'nothing here'
  generate
  local expected
  expected="$(printf '%s\n' '| a.cs | //#if | notifications |' '| b.md | <!--#if | includeSample |' '| c.yml | #if | caching == "redis" |')"
  assert_eq "$expected" "$(rows markers)" "markers_shard: the rows"
  local lines=()
  mapfile -t lines < "$idx/markers.md"
  assert_eq '<!-- Generated by build/scripts/project-index.sh; do not edit. Regenerate with: bash build/scripts/project-index.sh -->' \
    "${lines[0]}" "markers_shard: banner"
  assert_eq "" "${lines[1]}" "markers_shard: blank line after the banner"
  assert_eq "# Template switch markers" "${lines[2]}" "markers_shard: title"
  assert_eq "" "${lines[3]}" "markers_shard: blank line after the title"
  assert_eq "Rows: 3" "${lines[4]}" "markers_shard: Rows line"
  assert_eq "" "${lines[5]}" "markers_shard: blank line before the table"
  assert_eq "| file | kinds | expressions |" "${lines[6]}" "markers_shard: header row"
  assert_eq "| --- | --- | --- |" "${lines[7]}" "markers_shard: separator row"
}

case_markers_kinds_and_expressions() {
  make_tree
  put e.txt "$(printf '%%%%#if (a && b)\n%%%%#endif\n#if (c)\n#else\n#endif\n//#if (c)\n//#endif\n<!--#if (d || e)-->\n<!--#endif-->')"
  put f.cs "$(printf '//#if (x && (y))\n//#endif')"
  put g.cs "$(printf '//#else\n//#endif')"
  generate
  local expected
  expected="$(printf '%s\n' '| e.txt | #if, %%#if, //#if, <!--#if | a && b; c; d \|\| e |' '| f.cs | //#if | x && (y) |' '| g.cs | //#if | - |')"
  assert_eq "$expected" "$(rows markers)" "markers_kinds_and_expressions: joined kinds and expressions"
}

case_markers_zero_rows() {
  make_tree
  put a.cs 'int x;'
  generate
  assert_contains "Rows: 0" "$(cat "$idx/markers.md")" "markers_zero_rows: Rows: 0"
  assert_eq "" "$(rows markers)" "markers_zero_rows: no rows"
}

case_stub_shards() {
  make_tree
  put a.cs 'int x;'
  generate
  local s text
  for s in $shards; do
    if [[ -f "$idx/$s.md" ]]; then
      text="$(<"$idx/$s.md")"
      assert_contains "Rows: 0" "$text" "stub_shards: $s.md exists with zero rows"
    else
      echo "FAIL: stub_shards: $s.md was not written" >&2
      failures=$((failures + 1))
    fi
  done
  # The headers of the single-table stubs are the plan's column names; the two-table stubs show both tables.
  assert_contains $'\n| module | method | route | handler | permission | file |\n| --- | --- | --- | --- | --- | --- |' \
    "$(<"$idx/routes.md")" "stub_shards: routes header"
  assert_contains $'\n| code | module | admin default | file |\n' "$(<"$idx/permissions.md")" "stub_shards: permissions header"
  assert_contains $'\n| event | publisher | handlers |\n' "$(<"$idx/events.md")" "stub_shards: events header"
  assert_contains $'\n| job | module | config keys | file |\n' "$(<"$idx/jobs.md")" "stub_shards: jobs header"
  assert_contains $'\n| section | owner | documented in |\n' "$(<"$idx/config.md")" "stub_shards: config header"
  assert_eq $'Rows: 0\n\n## Declared codes\n\n| code | module | http | resource key | file |\n| --- | --- | --- | --- | --- |\n\n## Resource-only keys\n\n| key | http | resx file |\n| --- | --- | --- |' \
    "$(tail -n +5 "$idx/errors.md")" "stub_shards: errors shows both tables"
  assert_eq $'Rows: 0\n\n## Folders\n\n| test project | folder | source area | files |\n| --- | --- | --- | --- |\n\n## Name pairs\n\n| source | test |\n| --- | --- |' \
    "$(tail -n +5 "$idx/tests.md")" "stub_shards: tests shows both tables"
}

# Runs a snippet with the generator sourced, in a subshell with its own work and output directories: sets status and combined (the
# snippet's stdout and stderr). Used for the functions that need no tree.
sourced() {
  status=0
  combined="$( ( source "$gen"; work="$(mktemp -d "$tmp_root/work.XXXXXX")"; shard_dir="$work/out"; mkdir -p "$shard_dir"; eval "$1" ) 2>&1 )" || status=$?
}

case_two_table_shard() {
  sourced '
    printf "%s\n" "b|2" "a|1" "a|0" > "$work/r1"
    printf "%s\n" "z|y|x" > "$work/r2"
    shard_begin demo "Demo"
    shard_table "First" "" "k|v" "$work/r1"
    shard_table "Second" "A note, with a | pipe." "p|q|r" "$work/r2"
    shard_end
    cat "$shard_dir/demo.md"'
  local banner='<!-- Generated by build/scripts/project-index.sh; do not edit. Regenerate with: bash build/scripts/project-index.sh -->'
  assert_eq "0" "$status" "two_table_shard: exit code"
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 4\n\n## First\n\n| k | v |\n| --- | --- |\n| a | 0 |\n| a | 1 |\n| b | 2 |\n\n## Second\n\nA note, with a | pipe.\n\n| p | q | r |\n| --- | --- | --- |\n| z | y | x |' \
    "$combined" "two_table_shard: exact bytes (heading, note, tables, total of Rows)"
  # An empty first table still counts nothing and keeps its place; a note and a heading are both optional.
  sourced '
    : > "$work/r1"
    printf "%s\n" "a|1" > "$work/r2"
    shard_begin demo "Demo"
    shard_table "" "" "k|v" "$work/r1"
    shard_table "" "Only a note." "p|q" "$work/r2"
    shard_end
    cat "$shard_dir/demo.md"'
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 1\n\n| k | v |\n| --- | --- |\n\nOnly a note.\n\n| p | q |\n| --- | --- |\n| a | 1 |' \
    "$combined" "two_table_shard: empty first table, note without a heading"
  # write_shard keeps the one-table layout.
  sourced '
    printf "%s\n" "b|2" "a|1" > "$work/r1"
    write_shard demo "Demo" "k|v" "$work/r1"
    cat "$shard_dir/demo.md"'
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 2\n\n| k | v |\n| --- | --- |\n| a | 1 |\n| b | 2 |' \
    "$combined" "two_table_shard: write_shard single-table layout unchanged"
}

# A parse error on a later shard must leave --out as it was: nothing is copied before every shard succeeded.
case_atomic_write() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  must mkdir -p "$idx"
  printf 'old\n' > "$idx/markers.md"
  sourced "emit_jobs() { fail_parse 'boom in jobs'; }; main --root '$tree' --out '$idx'"
  assert_eq "2" "$status" "atomic_write: exit code"
  assert_contains "boom in jobs" "$combined" "atomic_write: names the failure"
  assert_eq "old" "$(<"$idx/markers.md")" "atomic_write: the existing shard is untouched"
  local written=("$idx"/*)
  assert_eq "1" "${#written[@]}" "atomic_write: no other shard was written"
  # Without the failure the same run writes everything.
  sourced "main --root '$tree' --out '$idx'"
  assert_eq "0" "$status" "atomic_write: a run without a failure succeeds"
  assert_contains "| a.cs | //#if | notifications |" "$(<"$idx/markers.md")" "atomic_write: the shard is replaced"
}

case_determinism() {
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put_raw dir/crlf.cs 'int x;\r\n//#if (includeSample)\r\nint y;\r\n//#endif\r\n'
  put_raw dir/crlf.md '<!--#if (notifications && includeSample)-->\r\ntext\r\n<!--#endif-->\r\n'
  generate
  local first second crlf_tree="$tree"
  first="$(cat "$idx"/*.md)"
  rm -rf "$idx"
  generate
  second="$(cat "$idx"/*.md)"
  assert_eq "$first" "$second" "determinism: two runs give identical bytes"
  assert_not_contains $'\r' "$(cat "$idx"/markers.md)" "determinism: no carriage return in the output"
  # The same files with LF line endings give the same output.
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put dir/crlf.cs "$(printf 'int x;\n//#if (includeSample)\nint y;\n//#endif')"
  put dir/crlf.md "$(printf '<!--#if (notifications && includeSample)-->\ntext\n<!--#endif-->')"
  generate
  assert_eq "$first" "$(cat "$idx"/*.md)" "determinism: CRLF files give the same output as LF files"
  # --check passes on the CRLF tree against the shards generated from the LF tree.
  write_map
  run_gen --check --root "$crlf_tree" --out "$idx"
  assert_eq "0" "$status" "determinism: --check passes across line endings"
}

case_untracked_ignored() {
  make_tree
  make_git_tree
  put tracked.cs "$(printf '//#if (notifications)\n//#endif')"
  put .gitignore 'obj/'
  must git -C "$tree" add tracked.cs .gitignore
  put untracked.cs "$(printf '//#if (includeSample)\n//#endif')"
  put obj/ignored.cs "$(printf '//#if (caching == "redis")\n//#endif')"
  generate
  assert_eq "| tracked.cs | //#if | notifications |" "$(rows markers)" "untracked_ignored: only the tracked file is listed"
  assert_contains "Rows: 1" "$(cat "$idx/markers.md")" "untracked_ignored: row count"
}

case_git_file_marker() {
  # In a git worktree .git is a file (gitdir: ...), not a directory: the file list must still come from git.
  make_tree
  must git init -q --separate-git-dir "$tmp_root/gitdir.$$" "$tree"
  must git -C "$tree" config core.autocrlf false
  [[ -f "$tree/.git" ]] || { echo "FAIL: setup: .git is not a file" >&2; exit 1; }
  put tracked.cs "$(printf '//#if (notifications)\n//#endif')"
  must git -C "$tree" add tracked.cs
  put untracked.cs "$(printf '//#if (includeSample)\n//#endif')"
  generate
  assert_eq "| tracked.cs | //#if | notifications |" "$(rows markers)" "git_file_marker: .git as a file still lists tracked files only"
  rm -rf "$tmp_root/gitdir.$$"
}

case_find_excludes_build_dirs() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  put bin/b.cs "$(printf '//#if (includeSample)\n//#endif')"
  put src/obj/c.cs "$(printf '//#if (includeSample)\n//#endif')"
  generate
  assert_eq "| a.cs | //#if | notifications |" "$(rows markers)" "find_excludes_build_dirs: bin and obj are skipped without git"
}

case_check_stale() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  generate
  write_map
  check
  assert_eq "0" "$status" "check_stale: a fresh tree passes"
  printf 'extra\n' >> "$idx/markers.md"
  check
  assert_eq "1" "$status" "check_stale: exit code"
  assert_contains "stale: markers" "$combined" "check_stale: names the shard"
  # The check rewrites nothing.
  assert_contains "extra" "$(cat "$idx/markers.md")" "check_stale: shard left as it was"
  # A change in the source makes the shard stale too.
  generate
  put b.cs "$(printf '//#if (includeSample)\n//#endif')"
  check
  assert_eq "1" "$status" "check_stale: a changed source"
  assert_contains "stale: markers" "$combined" "check_stale: names the shard after a source change"
}

case_check_missing_and_unknown() {
  make_tree
  put a.cs 'int x;'
  generate
  write_map
  rm -f "$idx/jobs.md"
  check
  assert_eq "1" "$status" "check_missing_and_unknown: exit code for a missing shard"
  assert_contains "missing: jobs" "$combined" "check_missing_and_unknown: names the missing shard"
  generate
  printf 'x\n' > "$idx/x.md"
  check
  assert_eq "1" "$status" "check_missing_and_unknown: exit code for an unknown file"
  assert_contains "unknown shard: x.md" "$combined" "check_missing_and_unknown: names the file"
  # A file that is not markdown is not a shard.
  rm -f "$idx/x.md"
  printf 'x\n' > "$idx/notes.txt"
  check
  assert_eq "0" "$status" "check_missing_and_unknown: a non-markdown file is ignored"
}

# map_body <first-line> [omitted shard] [filler lines]: sets body to a MAP.md that has the first line, then one line naming each shard
# (except the omitted one), then the filler lines.
map_body() {
  local s i
  body="$1"$'\n'
  for s in $shards; do
    if [[ "$s" != "${2-}" ]]; then body+="\`$s.md\`"$'\n'; fi
  done
  for (( i = 1; i <= ${3-0}; i++ )); do body+="line $i"$'\n'; done
}

# map_check: runs check_map of the sourced script on the current tree and idx (no generator run); sets status and combined.
map_check() {
  status=0
  combined="$( ( source "$gen"; root="$tree"; out="$idx"; check_map ) 2>&1 )" || status=$?
}

case_map_rules() {
  make_tree
  put src/exists.cs 'int x;'
  must mkdir -p "$tree/docs/dir" "$tree/.github"
  # MAP.md missing.
  map_check
  assert_eq "1" "$status" "map_rules: MAP.md missing: exit code"
  assert_contains "MAP.md" "$combined" "map_rules: MAP.md missing: named"
  # A valid MAP.md.
  map_body '# Map: `src/exists.cs` and `docs/dir/` and `.github/` are real.'
  write_map "$body"
  map_check
  assert_eq "0" "$status" "map_rules: a valid MAP.md passes"
  # The one end-to-end run: the CLI path of --check (generate, compare, then the MAP.md rules) on a valid MAP.md.
  generate
  check
  assert_eq "0" "$status" "map_rules: --check end to end passes"
  # A path that does not exist.
  map_body '# Map `src/Nope.cs`'
  write_map "$body"
  map_check
  assert_eq "1" "$status" "map_rules: a missing path: exit code"
  assert_contains "src/Nope.cs" "$combined" "map_rules: a missing path: named"
  # A shard not named in MAP.md.
  map_body '# Map' tests
  write_map "$body"
  map_check
  assert_eq "1" "$status" "map_rules: an unnamed shard: exit code"
  assert_contains "tests.md" "$combined" "map_rules: an unnamed shard: named"
  # 121 lines fail, 120 pass.
  local lines=()
  map_body '# Map' '' 112
  write_map "$body"
  mapfile -t lines < "$idx/MAP.md"
  assert_eq "121" "${#lines[@]}" "map_rules: setup: 121 lines"
  map_check
  assert_eq "1" "$status" "map_rules: 121 lines: exit code"
  assert_contains "121" "$combined" "map_rules: 121 lines: says how many"
  map_body '# Map' '' 111
  write_map "$body"
  mapfile -t lines < "$idx/MAP.md"
  assert_eq "120" "${#lines[@]}" "map_rules: setup: 120 lines"
  map_check
  assert_eq "0" "$status" "map_rules: 120 lines pass"
  # Tokens the rule ignores: a brace, a star, an angle bracket, an ellipsis, a space, and other prefixes.
  map_body '# Map `src/{Module}/x.cs` `src/*/y.cs` `src/<name>/z.cs` `src/.../w.cs` `docs/a b.md` `elsewhere/none.md` `x`'
  write_map "$body"
  map_check
  assert_eq "0" "$status" "map_rules: tokens with {, *, <, ... or a space, and other prefixes, are ignored"
  # A CRLF MAP.md gives the same result.
  map_body '# Map `src/exists.cs`'
  printf '%s' "${body//$'\n'/$'\r\n'}" > "$idx/MAP.md"
  map_check
  assert_eq "0" "$status" "map_rules: a CRLF MAP.md passes"
}

case_need_rows() {
  # need_rows is a function of the script; source it (the script runs main only when executed).
  local rc=0
  ( source "$gen"; need_rows routes 0 3 ) > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "need_rows: zero rows with a source is a parse error"
  rc=0
  ( source "$gen"; need_rows routes 0 0 ) > /dev/null 2>&1 || rc=$?
  assert_eq "0" "$rc" "need_rows: zero rows without a source is fine"
  rc=0
  ( source "$gen"; need_rows routes 5 3 ) > /dev/null 2>&1 || rc=$?
  assert_eq "0" "$rc" "need_rows: rows present is fine"
  local msg
  msg="$( ( source "$gen"; need_rows routes 0 3 ) 2>&1 || true)"
  assert_contains "routes" "$msg" "need_rows: names the shard"
}

# routes_fixture [with-b]: a tree with a Host Program.cs that maps module extensions on `api` (/api/v1) and one at the root, and module
# A: a module file calling the endpoints extension, a root group "a" with a "/" route, a route split over three lines, an anonymous route
# and a child group extension in another file (CRLF) that takes its prefix from the caller. With "with-b", module B maps a JWKS-like
# route at the root through a const string, with no handler parameter.
routes_fixture() {
  local program='var app = builder.Build();
var api = app.MapGroup("/api/v1");
api.MapAModuleEndpoints();'
  if [[ "${1-}" == "with-b" ]]; then program+=$'\napp.MapBWellKnownEndpoints();'; fi
  program+=$'\napp.Run();'
  put src/Host/H/Program.cs "$program"
  put src/Modules/A/A/AModule.cs 'public static class AModule
{
    public static IEndpointRouteBuilder MapAModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAEndpoints();
        return app;
    }
}'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        // A comment that mentions group.MapPut("ignored", Nothing) is not a route.
        var group = app.MapGroup("a")
            .WithTags("A");

        group.MapGet("/", ListAsync).RequirePermission(APermissions.View);

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequirePermission(APermissions.Approve)
            .WithIdempotency();

        group.MapPost("login", LoginAsync).AllowAnonymous(); // trailing comment

        group.MapChildEndpoints();
        return app;
    }

    /// <summary>Lists.</summary>
    private static async Task<IResult> ListAsync(
        [AsParameters] ListRequest request,
        IQueryHandler<ListAQuery, CursorPage<AItem>> handler,
        CancellationToken cancellationToken)
    {
        return TypedResults.Ok();
    }

    private static async Task<IResult> ApproveAsync(Guid id, ICommandHandler<ApproveACommand> handler) => TypedResults.NoContent();

    private static async Task<IResult> LoginAsync(ICommandHandler<LoginCommand, LoginResult> handler) => TypedResults.Ok();
}'
  put_raw src/Modules/A/A/Endpoints/ChildEndpoints.cs 'internal static class ChildEndpoints\r\n{\r\n    internal static RouteGroupBuilder MapChildEndpoints(this RouteGroupBuilder a)\r\n    {\r\n        var child = a.MapGroup("child");\r\n        child.MapDelete("/{id}", RemoveAsync)\r\n            .RequireAuthorization();\r\n        return a;\r\n    }\r\n\r\n    private static async Task<IResult> RemoveAsync(Guid id, ICommandHandler<RemoveCommand> handler) => TypedResults.NoContent();\r\n}\r\n'
  if [[ "${1-}" == "with-b" ]]; then
    put src/Modules/B/B/BModule.cs 'public static class BModule
{
    public static IEndpointRouteBuilder MapBWellKnownEndpoints(this IEndpointRouteBuilder app) => app.MapJwksEndpoints();
}'
    put src/Modules/B/B/Endpoints/BEndpoints.cs 'internal static class BEndpoints
{
    internal const string Route = "/.well-known/jwks.json";

    internal static IEndpointRouteBuilder MapJwksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, GetKeySet)
            .AllowAnonymous();
        return app;
    }

    private static IResult GetKeySet(ISigningKeyProvider keys) => TypedResults.Ok();
}'
  fi
}

# routes_of_tree: runs emit_routes of the sourced script on the current tree (no full generator run); sets status and combined (the
# sorted rows, or the error output).
routes_of_tree() {
  sourced "root='$tree'; cd '$tree'; prepare_files; emit_routes > \"\$work/routes.out\"; sort \"\$work/routes.out\""
}

routes_a_rows='A|DELETE|/api/v1/a/child/{id}|RemoveCommand|authenticated|src/Modules/A/A/Endpoints/ChildEndpoints.cs
A|GET|/api/v1/a|ListAQuery|APermissions.View|src/Modules/A/A/Endpoints/AEndpoints.cs
A|POST|/api/v1/a/login|LoginCommand|anonymous|src/Modules/A/A/Endpoints/AEndpoints.cs
A|POST|/api/v1/a/{id:guid}/approve|ApproveACommand|APermissions.Approve|src/Modules/A/A/Endpoints/AEndpoints.cs'

case_routes() {
  make_tree
  routes_fixture with-b
  routes_of_tree
  assert_eq "0" "$status" "routes: exit code"
  assert_eq "$routes_a_rows"$'\nB|GET|/.well-known/jwks.json|-|anonymous|src/Modules/B/B/Endpoints/BEndpoints.cs' "$combined" \
    "routes: exact rows (root route without //, three-line chain, child group prefix from the caller, root-mapped const route)"
  # The one end-to-end run of the routes shard: the rendered table.
  generate
  assert_contains $'Rows: 5\n\n| module | method | route | handler | permission | file |\n| --- | --- | --- | --- | --- | --- |\n| A | DELETE | /api/v1/a/child/{id} | RemoveCommand | authenticated | src/Modules/A/A/Endpoints/ChildEndpoints.cs |' \
    "$(<"$idx/routes.md")" "routes: rendered shard"
}

case_routes_zero() {
  make_tree
  put src/Host/H/Program.cs $'var api = app.MapGroup("/api/v1");\napi.MapAEndpoints();'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.Map("x", HandleAsync);
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: exit code"
  assert_contains "no routes parsed in src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: names the file"
  # A file with some routes and one mapping of an unknown shape fails too, rather than emitting fewer rows.
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.MapGet("x", GetAsync).RequireAuthorization();
        group.MapMethods("y", ["HEAD"], HeadAsync);
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: an unknown mapping beside known ones: exit code"
  assert_contains "src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: an unknown mapping: names the file"
  # An extension that nothing calls has no prefix: a parse error, not a guessed route.
  put src/Host/H/Program.cs 'var api = app.MapGroup("/api/v1");'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("x", GetAsync).RequireAuthorization();
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: an uncalled extension: exit code"
  assert_contains "src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: an uncalled extension: names the file"
}

case_routes_module_absent() {
  make_tree
  routes_fixture
  routes_of_tree
  assert_eq "0" "$status" "routes_module_absent: exit code"
  assert_eq "$routes_a_rows" "$combined" "routes_module_absent: only module A rows"
  # A tree with no endpoint file at all has no rows and no error.
  make_tree
  put src/Host/H/Program.cs 'var api = app.MapGroup("/api/v1");'
  routes_of_tree
  assert_eq "0" "$status" "routes_module_absent: no endpoint file: exit code"
  assert_eq "" "$combined" "routes_module_absent: no endpoint file: no rows"
}

# routes_one_module <endpoints file body> [extra Program.cs lines]: a fresh tree with a Program.cs that maps MapAEndpoints on /api/v1
# and module A's endpoint file with the given body.
routes_one_module() {
  make_tree
  put src/Host/H/Program.cs $'var api = app.MapGroup("/api/v1");\napi.MapAEndpoints();'"${2:+$'\n'$2}"
  put src/Modules/A/A/Endpoints/AEndpoints.cs "$1"
}

# routes_fails <label> <needle>: emit_routes on the current tree exits 2 with a message containing the needle.
routes_fails() {
  routes_of_tree
  assert_eq "2" "$status" "$1: exit code"
  assert_contains "$2" "$combined" "$1: message"
}

# Shapes that would give a wrong row with exit 0 fail loudly instead.
case_routes_loud() {
  local a='src/Modules/A/A/Endpoints/AEndpoints.cs'
  # An extension called from two places would have two prefixes.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("x", GetAsync).RequireAuthorization();
        return app;
    }
}' $'var v2 = app.MapGroup("/api/v2");\nv2.MapAEndpoints();'
  routes_fails "routes_loud: two callers" "extension MapAEndpoints called from two places in src/Host/H/Program.cs"
  # Two modules declaring an extension with the same name (legal in separate assemblies) would share one prefix.
  put src/Host/H/Program.cs $'var api = app.MapGroup("/api/v1");\napi.MapAEndpoints();'
  put src/Modules/C/C/Endpoints/CEndpoints.cs 'internal static class CEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("c", GetAsync).RequireAuthorization();
        return app;
    }
}'
  routes_fails "routes_loud: duplicate extension name" "extension MapAEndpoints declared twice: $a, src/Modules/C/C/Endpoints/CEndpoints.cs"
  # Group-level authorization is not read: its routes have no authorization call of their own.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a").RequirePermission(APermissions.View);
        group.MapGet("/", ListAsync);
        return app;
    }
}'
  routes_fails "routes_loud: group-level permission" "no authorization call on /api/v1/a in $a"
  # A route with no authorization call at all.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("x", GetAsync).WithName("X");
        return app;
    }
}'
  routes_fails "routes_loud: no authorization call" "no authorization call on /api/v1/x in $a"
  # An extension not named Map...Endpoints is not recognised: its routes must not fall back to the root.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapARoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.MapGet("x", GetAsync).RequireAuthorization();
        return app;
    }
}' 'api.MapARoutes();'
  routes_fails "routes_loud: extension not named Map...Endpoints" "cannot resolve the group prefix of group in $a"
  # The same with the signature split over two lines.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("x", GetAsync).RequireAuthorization();
        return app;
    }
}'
  routes_fails "routes_loud: split extension signature" "cannot resolve the group prefix of app in $a"
  # A helper method called with a nested group must not inherit the previous extension's scope.
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.MapGet("x", GetAsync).RequireAuthorization();
        var mfa = group.MapGroup("mfa");
        MapMfa(mfa);
        return app;
    }

    private static void MapMfa(RouteGroupBuilder group)
    {
        group.MapPost("verify", VerifyAsync).RequireAuthorization();
    }
}'
  routes_fails "routes_loud: helper method with a nested group" "cannot resolve the group prefix of group in $a"
}

# A named policy is the permission column; a comma inside a route literal stays in the route.
case_routes_shapes() {
  routes_one_module 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.MapGet("/{code:length(3,8)}", GetAsync).RequireAuthorization("Admin");
        group.MapGet("/r/{n:range(1,5)}", RangeAsync).AllowAnonymous();
        return app;
    }

    private static Task<IResult> GetAsync(string code, IQueryHandler<GetQuery, Item> handler) => null;

    private static Task<IResult> RangeAsync(int n, IQueryHandler<RangeQuery, Item> handler) => null;
}'
  routes_of_tree
  assert_eq "0" "$status" "routes_shapes: exit code"
  assert_eq 'A|GET|/api/v1/a/r/{n:range(1,5)}|RangeQuery|anonymous|src/Modules/A/A/Endpoints/AEndpoints.cs
A|GET|/api/v1/a/{code:length(3,8)}|GetQuery|Admin|src/Modules/A/A/Endpoints/AEndpoints.cs' "$combined" \
    "routes_shapes: policy name and commas inside the route"
}

# emit_of_tree <emit function...>: runs the emit functions of the sourced script on the current tree, one after the other (no full
# generator run); sets status and combined (the rows of each, sorted, each block followed by a "--" line, or the error output).
emit_of_tree() {
  local f snippet="root='$tree'; cd '$tree'; prepare_files"
  for f in "$@"; do snippet+="; $f > \"\$work/$f.out\"; sort \"\$work/$f.out\"; echo --"; done
  sourced "$snippet"
}

# emit_fails <label> <needle> <emit function...>: the emit functions on the current tree exit 2 with a message containing the needle.
emit_fails() {
  local label="$1" needle="$2"
  shift 2
  emit_of_tree "$@"
  assert_eq "2" "$status" "$label: exit code"
  assert_contains "$needle" "$combined" "$label: message"
}

# permissions_fixture: module A with three constants, a source in both shapes of the tree (a one-line Define with adminDefault and a
# multi-line new(...) with AdminDefault), a third constant with no source entry, and decoys (a comment and a string naming a constant,
# a parenthesis and a comma inside a string, the Define helper itself); module B in CRLF with an explicit AdminDefault: false.
permissions_fixture() {
  put src/Modules/A/A/Application/APermissions.cs 'namespace X;

/// <summary>The codes, <c>module.resource.action</c>; see APermissions.View.</summary>
internal static class APermissions
{
    public const string View = "a.thing.view";

    public const string Edit = "a.thing.edit"; // trailing comment

    public const string Delete = "a.thing.delete";
}'
  put src/Modules/A/A/Application/APermissionSource.cs 'internal sealed class APermissionSource : IPermissionSource
{
    private const string ModuleName = "a";
    private static readonly string Verbatim = @$"""x"" y";

    // A comment naming APermissions.Delete with AdminDefault: true is not a record.
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "Read (and list) things, not APermissions.Delete, AdminDefault: false.", adminDefault: true),
        new(
            APermissions.Edit,
            ModuleName, "Edit", "Change things",
            AdminDefault: true),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;

    private static PermissionDefinition Define(string code, string name, string description, bool adminDefault = false) =>
        new(code, ModuleName, name, description, adminDefault);
}'
  put_raw src/Modules/B/B/Application/BPermissions.cs 'internal static class BPermissions\r\n{\r\n    public const string ItemView = "b.item.view";\r\n}\r\n'
  put_raw src/Modules/B/B/Application/BPermissionSource.cs 'internal sealed class BPermissionSource : IPermissionSource\r\n{\r\n    private static readonly PermissionDefinition[] All =\r\n    [\r\n        new(\r\n            BPermissions.ItemView,\r\n            ModuleName,\r\n            "View items",\r\n            "List items.",\r\n            AdminDefault: false),\r\n    ];\r\n}\r\n'
}

permissions_rows='a.thing.delete|A|no|src/Modules/A/A/Application/APermissions.cs
a.thing.edit|A|yes|src/Modules/A/A/Application/APermissions.cs
a.thing.view|A|yes|src/Modules/A/A/Application/APermissions.cs
b.item.view|B|no|src/Modules/B/B/Application/BPermissions.cs'

case_permissions() {
  make_tree
  permissions_fixture
  # A test permission source outside src/Modules is not read.
  put tests/T/TestPermissionSource.cs 'internal sealed class TestPermissionSource { Define(TestPermissions.Nope, "x", "y"); }'
  emit_of_tree emit_permissions
  assert_eq "0" "$status" "permissions: exit code"
  assert_eq "$permissions_rows"$'\n--' "$combined" "permissions: exact rows (both shapes, absent flag is no, module from the folder, CRLF)"
  # The one end-to-end run of the permissions shard: the rendered table.
  generate
  assert_contains $'Rows: 4\n\n| code | module | admin default | file |\n| --- | --- | --- | --- |\n| a.thing.delete | A | no | src/Modules/A/A/Application/APermissions.cs |' \
    "$(<"$idx/permissions.md")" "permissions: rendered shard"
  # A tree without any permission file has no rows and no error.
  make_tree
  put src/Modules/A/A/Application/Other.cs 'class Other {}'
  emit_of_tree emit_permissions
  assert_eq "0" "$status" "permissions: no permission file: exit code"
  assert_eq "--" "$combined" "permissions: no permission file: no rows"
}

# Shapes that would give a wrong or missing permission row with exit 0 fail loudly instead.
case_permissions_loud() {
  local src='src/Modules/A/A/Application/APermissionSource.cs' consts='src/Modules/A/A/Application/APermissions.cs'
  # A source entry whose constant is not declared in any *Permissions.cs.
  make_tree
  permissions_fixture
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d", adminDefault: true),
        Define(APermissions.Missing, "Missing", "d"),
    ];
}'
  emit_fails "permissions_loud: undeclared constant" "APermissions.Missing in $src" emit_permissions
  # The same constant in two source entries.
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d", adminDefault: true),
        Define(APermissions.View, "View again", "d"),
    ];
}'
  emit_fails "permissions_loud: constant in two entries" "APermissions.View" emit_permissions
  # Two constants with the same code.
  permissions_fixture
  put "$consts" 'internal static class APermissions
{
    public const string View = "a.thing.view";
    public const string Read = "a.thing.view";
}'
  emit_fails "permissions_loud: duplicate code" "duplicate permission code a.thing.view" emit_permissions
  # A record shape the parser does not know (the constant is not the first argument of Define( or new().
  permissions_fixture
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d", adminDefault: true),
        new PermissionDefinition(APermissions.Edit, ModuleName, "Edit", "d", AdminDefault: true),
    ];
}'
  emit_fails "permissions_loud: unknown record shape" "$src" emit_permissions
  # A positional admin flag, and a flag that is not a literal.
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        new(APermissions.View, ModuleName, "View", "d", true),
    ];
}'
  emit_fails "permissions_loud: positional flag" "$src" emit_permissions
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        new(APermissions.View, ModuleName, "View", "d", AdminDefault: isAdmin),
    ];
}'
  emit_fails "permissions_loud: flag not a literal" "$src" emit_permissions
  # A helper whose flag defaults to true would make an absent flag mean yes.
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d"),
    ];

    private static PermissionDefinition Define(string code, string name, string description, bool adminDefault = true) =>
        new(code, ModuleName, name, description, adminDefault);
}'
  emit_fails "permissions_loud: helper default true" "$src" emit_permissions
  # A constant split over two lines, and a code that is not module.resource.action.
  permissions_fixture
  put "$consts" 'internal static class APermissions
{
    public const string View = "a.thing.view";
    public const string Edit =
        "a.thing.edit";
}'
  emit_fails "permissions_loud: split constant" "$consts" emit_permissions
  put "$consts" 'internal static class APermissions
{
    public const string View = "a.thing.view";
    public const string Edit = "a.edit";
}'
  emit_fails "permissions_loud: two-segment code" "$consts" emit_permissions
  # A source file with no record at all, and a constants file with no constant.
  permissions_fixture
  put "$src" 'class APermissionSource { }'
  emit_fails "permissions_loud: source without records" "$src" emit_permissions
  permissions_fixture
  put "$consts" 'internal static class APermissions { }'
  emit_fails "permissions_loud: constants file without constants" "$consts" emit_permissions
  # An unqualified constant under `using static`, and an aliased class, are not records: their admin flag would be lost.
  permissions_fixture
  put "$src" 'using static A.APermissions;

class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.Edit, "Edit", "d"),
        Define(View, "View", "d", adminDefault: true),
    ];
}'
  emit_fails "permissions_loud: using static" "admin default flag(s) outside a parsed permission record in $src" emit_permissions
  put "$src" 'using P = A.APermissions;

class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.Edit, "Edit", "d"),
        new(P.View, ModuleName, "View", "d", AdminDefault: true),
    ];
}'
  emit_fails "permissions_loud: aliased class" "admin default flag(s) outside a parsed permission record in $src" emit_permissions
  # `with { AdminDefault = true }` is a shape the parser does not know (not a parameter default).
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d") with { AdminDefault = true },
    ];
}'
  emit_fails "permissions_loud: with expression" "AdminDefault assigned with = at line 5 of $src" emit_permissions
  # A renamed or moved source file: the module has constants and no *PermissionSource.cs.
  permissions_fixture
  rm -f "$tree/$src"
  put src/Modules/A/A/Application/AAccessSource.cs 'class AAccessSource { }'
  emit_fails "permissions_loud: no source file" "module A has the constants file $consts but no *PermissionSource.cs" emit_permissions
  # A module whose source names only another module's constants: no record names its own.
  permissions_fixture
  put src/Modules/B/B/Application/BPermissions.cs 'internal static class BPermissions
{
    public const string ItemView = "b.item.view";
    public const string ItemEdit = "b.item.edit";
}'
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(BPermissions.ItemEdit, "Edit", "d", adminDefault: true),
    ];
}'
  emit_fails "permissions_loud: no record for the module" "module A has the constants file $consts but no parsed record" emit_permissions
  # A code in a `static readonly string` (not a const) is not read: it fails.
  permissions_fixture
  put "$consts" 'internal static class APermissions
{
    public const string View = "a.thing.view"; public const string Edit = "a.thing.edit";
    public static readonly string Delete = "a.thing.delete";
}'
  emit_fails "permissions_loud: static readonly code" "\"a.thing.delete\" at line 4 of $consts" emit_permissions
  # A constants file outside Application/ is not read, so a source naming its class fails.
  permissions_fixture
  put src/Modules/A/A/Domain/RolePermissions.cs 'internal static class RolePermissions
{
    public const string Admin = "a.role.admin";
}'
  put "$src" 'class APermissionSource
{
    private static readonly PermissionDefinition[] All =
    [
        Define(APermissions.View, "View", "d", adminDefault: true),
        Define(RolePermissions.Admin, "Admin", "d"),
    ];
}'
  emit_fails "permissions_loud: class outside Application" "the class RolePermissions of RolePermissions.Admin in $src is not declared" emit_permissions
}

# Shapes read correctly: two constants on one line; a *Permissions.cs outside Application/ (any content) is not a constants file.
case_permissions_shapes() {
  make_tree
  permissions_fixture
  put src/Modules/A/A/Application/APermissions.cs 'internal static class APermissions
{
    public const string View = "a.thing.view"; public const string Edit = "a.thing.edit";
    public const string Delete = "a.thing.delete";
}'
  put src/Modules/A/A/Domain/RolePermissions.cs 'internal static class RolePermissions
{
    public static readonly string[] All = ["a.role.admin"];
    public const int Count = 1;
}'
  emit_of_tree emit_permissions
  assert_eq "0" "$status" "permissions_shapes: exit code"
  assert_eq "$permissions_rows"$'\n--' "$combined" "permissions_shapes: two constants on one line, Domain/RolePermissions.cs ignored"
}

# The runner refuses a case name it does not know rather than passing without running it.
case_unknown_case_name() {
  local rc=0 msg
  msg="$(bash "$test_script" case_permissions_shapes case_bogus 2>&1)" || rc=$?
  assert_eq "2" "$rc" "unknown_case_name: exit code"
  assert_contains "unknown case: case_bogus" "$msg" "unknown_case_name: names the case"
  assert_not_contains "permissions_shapes" "$msg" "unknown_case_name: nothing ran"
}

# errors_fixture: module A with the three declaration shapes (literal on the same line, literal on the next line, a const string) and
# decoys (a doc comment quoting a code, a comma and a parenthesis inside a message, an escaped quote); a building block in CRLF with a
# qualified factory call, Unavailable and Failure; neutral resx files (one with an XML comment holding a <data> example, one CRLF) and a
# culture file that is ignored.
errors_fixture() {
  put src/Modules/A/A/Domain/AErrors.cs 'internal static class AErrors
{
    /// <summary>See <see cref="Busy"/>, code "a.doc".</summary>
    private static readonly string Verbatim = @$"""x"" y";
    public static readonly Error Missing = Error.NotFound("a.missing", "Not found, (really).");

    public static readonly Error Busy = Error.Conflict(
        "a.busy",
        "Busy, try \"later\" (soon).");

    public const string BusyCode = "a.locked";

    public static Error Locked(Guid id) =>
        Error.Validation(BusyCode, $"Thing {id} is locked, \"{id}\".") with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}'
  put_raw src/BuildingBlocks/TemplateName.Application.Common/Paging/PagingErrors.cs 'public static class PagingErrors\r\n{\r\n    public static readonly Error Down = SharedKernel.Error.Unavailable("paging.down", "Down.");\r\n\r\n    public static readonly Error Fail = Error.Failure(\r\n        "paging.fail",\r\n        "Failed.");\r\n}\r\n'
  put src/Modules/A/A/Resources/AErrorMessages.resx '<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    Example: <data name="Name1"><value>this is my long string</value></data>
  -->
  <data name="a.missing" xml:space="preserve">
    <value>Not found.</value>
  </data>
  <data name="a.busy" xml:space="preserve">
    <value>Busy.</value>
  </data>
  <data name="a.locked" xml:space="preserve">
    <value>Locked.</value>
  </data>
  <data name="http.404" xml:space="preserve">
    <value>Not found.</value>
  </data>
  <data name="validation.failed" xml:space="preserve">
    <value>Invalid.</value>
  </data>
</root>'
  put src/Modules/A/A/Resources/AErrorMessages.ms.resx '<root>
  <data name="ms.only" xml:space="preserve">
    <value>x</value>
  </data>
  <data
</root>'
  put_raw src/BuildingBlocks/TemplateName.Web.Common/Resources/CommonErrorMessages.resx '<root>\r\n  <data name="paging.down" xml:space="preserve">\r\n    <value>Down.</value>\r\n  </data>\r\n</root>\r\n'
}

errors_rows='a.busy|A|409|a.busy|src/Modules/A/A/Domain/AErrors.cs
a.locked|A|400|a.locked|src/Modules/A/A/Domain/AErrors.cs
a.missing|A|404|a.missing|src/Modules/A/A/Domain/AErrors.cs
paging.down|Application.Common|503|paging.down|src/BuildingBlocks/TemplateName.Application.Common/Paging/PagingErrors.cs
paging.fail|Application.Common|500|-|src/BuildingBlocks/TemplateName.Application.Common/Paging/PagingErrors.cs
--
http.404|404|src/Modules/A/A/Resources/AErrorMessages.resx
validation.failed|-|src/Modules/A/A/Resources/AErrorMessages.resx
--'

case_errors() {
  make_tree
  errors_fixture
  emit_of_tree emit_errors emit_errors_2
  assert_eq "0" "$status" "errors: exit code"
  assert_eq "$errors_rows" "$combined" "errors: exact rows of both tables (three shapes, statuses, building block, culture file ignored)"
  # emit_errors_2 alone gives the same second table.
  emit_of_tree emit_errors_2
  assert_eq "${errors_rows#*--$'\n'}" "$combined" "errors: emit_errors_2 on its own"
  # The one end-to-end run of the errors shard: both rendered tables and the total.
  generate
  local text
  text="$(<"$idx/errors.md")"
  assert_contains $'Rows: 7\n\n## Declared codes\n\n| code | module | http | resource key | file |\n| --- | --- | --- | --- | --- |\n| a.busy | A | 409 | a.busy | src/Modules/A/A/Domain/AErrors.cs |' \
    "$text" "errors: rendered first table"
  assert_contains $'## Resource-only keys\n\n' "$text" "errors: second heading"
  assert_contains $'| key | http | resx file |\n| --- | --- | --- |\n| http.404 | 404 | src/Modules/A/A/Resources/AErrorMessages.resx |\n| validation.failed | - | src/Modules/A/A/Resources/AErrorMessages.resx |' \
    "$text" "errors: rendered second table"
  # Read correctly too: the parenthesis on the line after the factory name, a constant qualified with the file's own class, a code
  # argument on the line after a parenthesis that ends a line, and a factory call with one argument.
  make_tree
  put src/Modules/C/C/Domain/CErrors.cs 'internal static class CErrors
{
    public const string Code = "c.own";
    public static readonly Error Next = Error.Conflict
        ("c.next", "m");
    public static readonly Error Own = Error.Forbidden(CErrors.Code, "m");
    public static readonly Error One = Error.Unauthorized(
        "c.one");
}'
  emit_of_tree emit_errors
  assert_eq $'c.next|C|409|-|src/Modules/C/C/Domain/CErrors.cs\nc.one|C|401|-|src/Modules/C/C/Domain/CErrors.cs\nc.own|C|403|-|src/Modules/C/C/Domain/CErrors.cs\n--' \
    "$combined" "errors: other shapes read correctly"
  # A tree without error files has no rows and no error.
  make_tree
  put src/Modules/A/A/Domain/Other.cs 'class Other {}'
  emit_of_tree emit_errors emit_errors_2
  assert_eq "0" "$status" "errors: no error file: exit code"
  assert_eq $'--\n--' "$combined" "errors: no error file: no rows"
}

case_errors_zero() {
  make_tree
  put src/Modules/A/A/Domain/AErrors.cs 'internal static class AErrors
{
    public static readonly Error X = new("a.x", "Message.", ErrorType.Conflict);
}'
  emit_fails "errors_zero: a declaration the parser does not know" "src/Modules/A/A/Domain/AErrors.cs" emit_errors
  put src/Modules/A/A/Domain/AErrors.cs 'internal static class AErrors
{
}'
  emit_fails "errors_zero: no declaration at all" "src/Modules/A/A/Domain/AErrors.cs" emit_errors
  # The CLI path: exit 2.
  run_gen --root "$tree" --out "$idx"
  assert_eq "2" "$status" "errors_zero: generator exit code"
  assert_contains "src/Modules/A/A/Domain/AErrors.cs" "$err" "errors_zero: generator names the file"
}

# Shapes that would give a wrong or missing error row with exit 0 fail loudly instead.
case_errors_loud() {
  local a='src/Modules/A/A/Domain/AErrors.cs' r='src/Modules/A/A/Resources/AErrorMessages.resx'
  # A file with parsed declarations and one whose constant cannot be resolved.
  make_tree
  errors_fixture
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static Error Other() => Error.NotFound(OtherErrors.Code, "m");
}'
  emit_fails "errors_loud: unresolved constant" "$a" emit_errors
  # An interpolated or verbatim code.
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static Error Other(string x) => Error.NotFound($"a.{x}", "m");
}'
  emit_fails "errors_loud: interpolated code" "$a" emit_errors
  # A constant qualified with another class is not resolved through a same-named constant of this file.
  put "$a" 'internal static class AErrors
{
    public const string Code = "a.missing";
    public static readonly Error Missing = Error.NotFound(Code, "m");
    public static Error Other() => Error.NotFound(OtherErrors.Code, "m");
}'
  emit_fails "errors_loud: constant of another class" "OtherErrors.Code of Error.NotFound at line 5 of $a" emit_errors
  # A factory the status map does not know.
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error Tea = Error.Teapot("a.tea", "m");
}'
  emit_fails "errors_loud: unknown factory" "$a" emit_errors
  # A code-shaped literal that no Error.<Factory> call declares (another declaration shape).
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error X = new("a.x", "m", ErrorType.Conflict);
}'
  emit_fails "errors_loud: code outside a factory call" "$a" emit_errors
  # Named arguments are not read.
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error Busy = Error.Conflict(code: "a.busy", message: "m");
}'
  emit_fails "errors_loud: named arguments" "$a" emit_errors
  # The same code declared twice, in two files and in one.
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
}'
  put src/Modules/A/A/Domain/BErrors.cs 'internal static class BErrors
{
    public static readonly Error Missing = Error.NotFound(
        "a.missing", "m");
}'
  emit_fails "errors_loud: duplicate code in two files" "duplicate error code a.missing" emit_errors
  rm -f "$tree/src/Modules/A/A/Domain/BErrors.cs"
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error Again = Error.Conflict("a.missing", "m");
}'
  emit_fails "errors_loud: duplicate code in one file" "duplicate error code a.missing" emit_errors
  # A neutral resx entry split over two lines, and a key declared twice.
  errors_fixture
  put "$r" '<root>
  <data
      name="a.missing" xml:space="preserve">
    <value>x</value>
  </data>
</root>'
  emit_fails "errors_loud: split resx entry" "$r" emit_errors
  put "$r" '<root>
  <data name="a.missing" xml:space="preserve"><value>x</value></data>
  <data name="a.missing" xml:space="preserve"><value>y</value></data>
</root>'
  emit_fails "errors_loud: duplicate resx key" "$r" emit_errors_2
  put "$r" '<root>
</root>'
  emit_fails "errors_loud: neutral resx without keys" "$r" emit_errors
  # An unknown declaration shape with a hyphenated or an uppercase code still fails (same charset as a code).
  errors_fixture
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error Y = new("a.y-z", "m", ErrorType.Conflict);
}'
  emit_fails "errors_loud: hyphenated code in another shape" "\"a.y-z\" at line 4 of $a" emit_errors
  put "$a" 'internal static class AErrors
{
    public static readonly Error Missing = Error.NotFound("a.missing", "m");
    public static readonly Error U = Create("A.Upper", "m");
}'
  emit_fails "errors_loud: uppercase code in another shape" "\"A.Upper\" at line 4 of $a" emit_errors
}

# events_fixture: the plumbing of the tree (the root interface, the publisher and handler interfaces, an in-process publisher with a
# generic PublishAsync declaration and casts to IIntegrationEventHandler<TEvent>); three events in module A's contracts (a one-line
# record, a multi-line record with a comma, a parenthesis and the base list inside a string default, and a CRLF record whose base list is
# on the next line); a publisher in module A (a multi-line call and a qualified one-line call) and one in a building block in CRLF (two
# calls of the same event); a handler in module B whose base list is on the line after the primary constructor, a handler in module C
# with a qualified event behind another interface; decoys in comments, a /* */ block and strings; a handler and an event under tests/.
events_fixture() {
  put src/BuildingBlocks/TemplateName.SharedKernel/IIntegrationEvent.cs 'namespace X;

/// <summary>Every integration event, for example <c>record FakeIntegrationEvent(Guid Id) : IIntegrationEvent;</c>.</summary>
public interface IIntegrationEvent
{
    Guid Id { get; }
}'
  put src/BuildingBlocks/TemplateName.Application.Common/Messaging/IIntegrationEventPublisher.cs 'public interface IIntegrationEventPublisher
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}'
  put src/BuildingBlocks/TemplateName.Application.Common/Messaging/IIntegrationEventHandler.cs 'public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}'
  put src/BuildingBlocks/TemplateName.Infrastructure.Common/Messaging/InProcessPublisher.cs 'internal sealed class InProcessPublisher(IServiceProvider provider) : IIntegrationEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        var handler = (IIntegrationEventHandler<TEvent>)provider.GetRequiredService(typeof(IIntegrationEventHandler<TEvent>));
        await handler.HandleAsync(integrationEvent, cancellationToken);
    }
}'
  put src/Modules/A/A.Contracts/IntegrationEvents/ThingHappenedIntegrationEvent.cs 'namespace A.Contracts.IntegrationEvents;

/// <summary>A thing happened; not <c>record FakeIntegrationEvent(Guid Id) : IIntegrationEvent;</c>.</summary>
public sealed record ThingHappenedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;'
  put src/Modules/A/A.Contracts/IntegrationEvents/ThingDoneIntegrationEvent.cs 'namespace A.Contracts.IntegrationEvents;

/* An old shape, kept as a note:
public sealed record ThingUndoneIntegrationEvent(Guid Id) : IIntegrationEvent;
*/
public sealed record ThingDoneIntegrationEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Note = "a (note), with ) : IIntegrationEvent;") : IIntegrationEvent;'
  put_raw src/Modules/A/A.Contracts/IntegrationEvents/QuietIntegrationEvent.cs 'public sealed record QuietIntegrationEvent(Guid Id)\r\n    : TemplateName.SharedKernel.IIntegrationEvent;\r\n'
  put src/Modules/A/A/Application/PublishThing.cs 'internal sealed class PublishThing(IIntegrationEventPublisher publisher)
{
    // publisher.PublishAsync(new GhostIntegrationEvent(Guid.Empty), ct) is a comment, not a call.
    private const string Text = "publisher.PublishAsync(new GhostIntegrationEvent(";
    private static readonly string Verbatim2 = $@"""x"" y";
    private static readonly string Raw = $"""a "quoted" {Text}""";
    private static readonly string Raw2 = $$"""a "quoted" {{Text}}""";
    private static readonly string Verbatim = @$"""x"" y";

    public Task Happened(CancellationToken ct)
        => publisher.PublishAsync(
            new ThingHappenedIntegrationEvent(
                Guid.NewGuid(),
                DateTimeOffset.UnixEpoch),
            ct);

    public Task Done(CancellationToken ct) => publisher.PublishAsync(new A.Contracts.IntegrationEvents.ThingDoneIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "x"), ct);
}'
  put_raw src/BuildingBlocks/TemplateName.Infrastructure.Common/Things/PublishDone.cs 'internal sealed class PublishDone(IIntegrationEventPublisher publisher)\r\n{\r\n    public async Task Run(CancellationToken ct)\r\n    {\r\n        await publisher.PublishAsync(\r\n            new ThingDoneIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "one"),\r\n            ct);\r\n        await publisher.PublishAsync(new ThingDoneIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "two"), ct);\r\n    }\r\n}\r\n'
  put src/Modules/B/B/Application/OnThing.cs 'internal sealed class OnThing(
    IUnitOfWork unitOfWork)
    : IIntegrationEventHandler<ThingHappenedIntegrationEvent>
{
    // class OnGhost : IIntegrationEventHandler<GhostIntegrationEvent> is a comment.
    public Task HandleAsync(ThingHappenedIntegrationEvent integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}'
  put src/Modules/C/C/Application/OnThingAgain.cs 'internal sealed class OnThingAgain(ILogger<OnThingAgain> logger, Func<int, bool> check)
    : IDisposable,
      IIntegrationEventHandler<A.Contracts.IntegrationEvents.ThingDoneIntegrationEvent>
{
    public void Dispose() { }
}'
  put tests/B.Tests/FakeHandler.cs 'internal sealed class FakeHandler : IIntegrationEventHandler<TestOnlyIntegrationEvent> { }
public sealed record TestOnlyIntegrationEvent(Guid Id) : IIntegrationEvent;'
}

events_rows='QuietIntegrationEvent|-|-
ThingDoneIntegrationEvent|A (src/Modules/A/A/Application/PublishThing.cs), Infrastructure.Common (src/BuildingBlocks/TemplateName.Infrastructure.Common/Things/PublishDone.cs)|C (src/Modules/C/C/Application/OnThingAgain.cs)
ThingHappenedIntegrationEvent|A (src/Modules/A/A/Application/PublishThing.cs)|B (src/Modules/B/B/Application/OnThing.cs)'

case_events() {
  make_tree
  events_fixture
  emit_of_tree emit_events
  assert_eq "0" "$status" "events: exit code"
  assert_eq "$events_rows"$'\n--' "$combined" "events: exact rows (record shapes, publishers, handlers, - when none, tests/ ignored)"
  # The one end-to-end run of the events shard: the rendered table.
  generate
  assert_contains $'Rows: 3\n\n| event | publisher | handlers |\n| --- | --- | --- |\n| QuietIntegrationEvent | - | - |\n| ThingDoneIntegrationEvent | A (src/Modules/A/A/Application/PublishThing.cs), Infrastructure.Common (src/BuildingBlocks/TemplateName.Infrastructure.Common/Things/PublishDone.cs) | C (src/Modules/C/C/Application/OnThingAgain.cs) |' \
    "$(<"$idx/events.md")" "events: rendered shard"
  # Two handlers of one event in two modules are joined in order; a handler of two events gives an entry for each.
  put src/Modules/D/D/Application/OnBoth.cs 'internal sealed class OnBoth : IIntegrationEventHandler<ThingHappenedIntegrationEvent>, IIntegrationEventHandler<QuietIntegrationEvent>
{
}'
  emit_of_tree emit_events
  assert_eq 'QuietIntegrationEvent|-|D (src/Modules/D/D/Application/OnBoth.cs)
ThingDoneIntegrationEvent|A (src/Modules/A/A/Application/PublishThing.cs), Infrastructure.Common (src/BuildingBlocks/TemplateName.Infrastructure.Common/Things/PublishDone.cs)|C (src/Modules/C/C/Application/OnThingAgain.cs)
ThingHappenedIntegrationEvent|A (src/Modules/A/A/Application/PublishThing.cs)|B (src/Modules/B/B/Application/OnThing.cs), D (src/Modules/D/D/Application/OnBoth.cs)
--' "$combined" "events: several handlers joined, one class handling two events"
  # Files named like events or handlers that declare an abstract base or an interface of that name pass and give no row.
  rm -f "$tree/src/Modules/D/D/Application/OnBoth.cs"
  put src/Modules/A/A.Contracts/IntegrationEvents/BaseIntegrationEvent.cs 'public abstract record BaseIntegrationEvent(Guid Id) : IIntegrationEvent;'
  put src/Modules/A/A.Contracts/IntegrationEvents/IAuthIntegrationEvent.cs 'public interface IAuthIntegrationEvent : IIntegrationEvent
{
    Guid UserId { get; }
}'
  put src/Modules/B/B/Application/BaseIntegrationEventHandler.cs 'internal abstract class BaseIntegrationEventHandler<TEvent> : IIntegrationEventHandler<TEvent>
    where TEvent : IIntegrationEvent
{
}'
  put src/Modules/B/B/Application/IAuthIntegrationEventHandler.cs 'public interface IAuthIntegrationEventHandler<in TEvent> : IIntegrationEventHandler<TEvent>
    where TEvent : IAuthIntegrationEvent
{
}'
  emit_of_tree emit_events
  assert_eq "0" "$status" "events: base and interface files: exit code"
  assert_eq "$events_rows"$'\n--' "$combined" "events: base and interface files: no row, no error"
  # A tree without any event has no rows and no error.
  make_tree
  put src/Modules/A/A/Application/Other.cs 'class Other {}'
  emit_of_tree emit_events
  assert_eq "0" "$status" "events: no event: exit code"
  assert_eq "--" "$combined" "events: no event: no rows"
}

case_events_zero() {
  make_tree
  put src/Modules/A/A.Contracts/IntegrationEvents/ThingHappenedIntegrationEvent.cs 'public sealed record ThingHappenedIntegrationEvent(Guid Id) : IEvent;'
  emit_fails "events_zero: an event file the parser cannot read" "src/Modules/A/A.Contracts/IntegrationEvents/ThingHappenedIntegrationEvent.cs" emit_events
  # The CLI path: exit 2.
  run_gen --root "$tree" --out "$idx"
  assert_eq "2" "$status" "events_zero: generator exit code"
  assert_contains "src/Modules/A/A.Contracts/IntegrationEvents/ThingHappenedIntegrationEvent.cs" "$err" "events_zero: generator names the file"
}

# Shapes that would give a wrong or missing event row with exit 0 fail loudly instead.
case_events_loud() {
  local p='src/Modules/A/A/Application/PublishThing.cs'
  # A published type that is not a declared event.
  make_tree
  events_fixture
  put "$p" 'internal sealed class PublishThing(IIntegrationEventPublisher publisher)
{
    public Task Happened(CancellationToken ct) => publisher.PublishAsync(new ThingHappenedIntegrationEvent(Guid.NewGuid(), default), ct);
    public Task Ghost(CancellationToken ct) => publisher.PublishAsync(new GhostIntegrationEvent(Guid.NewGuid()), ct);
}'
  emit_fails "events_loud: unknown published event" "GhostIntegrationEvent, which is not an integration event declared under src/" emit_events
  assert_contains "$p" "$combined" "events_loud: unknown published event names the file"
  # A variable passed to PublishAsync: the event cannot be told.
  put "$p" 'internal sealed class PublishThing(IIntegrationEventPublisher publisher)
{
    public Task Happened(CancellationToken ct)
    {
        var e = new ThingHappenedIntegrationEvent(Guid.NewGuid(), default);
        return publisher.PublishAsync(e, ct);
    }
}'
  emit_fails "events_loud: a variable published" "$p" emit_events
  # An explicit type argument.
  put "$p" 'internal sealed class PublishThing(IIntegrationEventPublisher publisher)
{
    public Task Happened(ThingHappenedIntegrationEvent e, CancellationToken ct) => publisher.PublishAsync<ThingHappenedIntegrationEvent>(e, ct);
}'
  emit_fails "events_loud: an explicit type argument" "$p" emit_events
  # An event created outside a PublishAsync call (another publishing path) in a file without PublishAsync.
  events_fixture
  put src/Modules/A/A/Application/Enqueue.cs 'internal sealed class Enqueue(IOutbox outbox)
{
    public void Run() => outbox.Add(new ThingHappenedIntegrationEvent(Guid.Empty, default));
}'
  emit_fails "events_loud: an event created outside PublishAsync" "ThingHappenedIntegrationEvent is created at line 3 of src/Modules/A/A/Application/Enqueue.cs" emit_events
  rm -f "$tree/src/Modules/A/A/Application/Enqueue.cs"
  # A handler of a type that is not a declared event.
  put src/Modules/B/B/Application/OnGhost.cs 'internal sealed class OnGhost
    : IIntegrationEventHandler<GhostIntegrationEvent>
{
}'
  emit_fails "events_loud: a handler of an unknown event" "src/Modules/B/B/Application/OnGhost.cs" emit_events
  rm -f "$tree/src/Modules/B/B/Application/OnGhost.cs"
  # A handler reference outside a class base list (a manual registration).
  put src/Modules/B/B/BModule.cs 'public static class BModule
{
    public static void Add(IServiceCollection services) => services.AddScoped<IIntegrationEventHandler<ThingHappenedIntegrationEvent>, OnThing>();
}'
  emit_fails "events_loud: a handler reference outside a base list" "src/Modules/B/B/BModule.cs" emit_events
  rm -f "$tree/src/Modules/B/B/BModule.cs"
  # A handler file whose class the parser does not see as a handler (a base class in between).
  put src/Modules/B/B/Application/ThingHappenedIntegrationEventHandler.cs 'internal sealed class ThingHappenedIntegrationEventHandler : HandlerBase<ThingHappenedIntegrationEvent>
{
}'
  emit_fails "events_loud: a handler file with no parsed handler" "src/Modules/B/B/Application/ThingHappenedIntegrationEventHandler.cs" emit_events
  rm -f "$tree/src/Modules/B/B/Application/ThingHappenedIntegrationEventHandler.cs"
  # The same event declared twice.
  put src/Modules/D/D.Contracts/ThingHappenedIntegrationEvent.cs 'public sealed record ThingHappenedIntegrationEvent(Guid Id) : IIntegrationEvent;'
  emit_fails "events_loud: an event declared twice" "the integration event ThingHappenedIntegrationEvent is declared twice" emit_events
  rm -f "$tree/src/Modules/D/D.Contracts/ThingHappenedIntegrationEvent.cs"
  # An event file that declares another name.
  put_raw src/Modules/A/A.Contracts/IntegrationEvents/QuietIntegrationEvent.cs 'public sealed record QuieterIntegrationEvent(Guid Id) : IIntegrationEvent;\r\n'
  emit_fails "events_loud: an event file declaring another name" "src/Modules/A/A.Contracts/IntegrationEvents/QuietIntegrationEvent.cs" emit_events
  assert_contains "rename the file or declare a type named QuietIntegrationEvent" "$combined" "events_loud: an event file declaring another name: says what was expected"
  # A class deriving from a generic handler base handles an event the parser cannot read.
  events_fixture
  put src/Modules/B/B/Application/BaseIntegrationEventHandler.cs 'internal abstract class BaseIntegrationEventHandler<TEvent> : IIntegrationEventHandler<TEvent>
{
}'
  put src/Modules/B/B/Application/OnQuiet.cs 'internal sealed class OnQuiet : BaseIntegrationEventHandler<QuietIntegrationEvent>
{
}'
  emit_fails "events_loud: a handler through a generic base" "handler through a derived generic interface/base: OnQuiet in src/Modules/B/B/Application/OnQuiet.cs" emit_events
  rm -f "$tree/src/Modules/B/B/Application/BaseIntegrationEventHandler.cs" "$tree/src/Modules/B/B/Application/OnQuiet.cs"
  # The same through a generic interface (the runtime scan registers OnLogin: GetInterfaces() is transitive).
  put src/Modules/A/A.Contracts/IntegrationEvents/LoginIntegrationEvent.cs 'public sealed record LoginIntegrationEvent(Guid Id) : IIntegrationEvent;'
  put src/Modules/B/B/Application/IAuthIntegrationEventHandler.cs 'public interface IAuthIntegrationEventHandler<in TEvent> : IIntegrationEventHandler<TEvent>
{
}'
  put src/Modules/B/B/Application/OnLogin.cs 'internal sealed class OnLogin : IAuthIntegrationEventHandler<LoginIntegrationEvent> { }'
  emit_fails "events_loud: a handler through a generic interface" "handler through a derived generic interface/base: OnLogin in src/Modules/B/B/Application/OnLogin.cs" emit_events
  rm -f "$tree/src/Modules/B/B/Application/IAuthIntegrationEventHandler.cs" "$tree/src/Modules/B/B/Application/OnLogin.cs"
  # A non-generic abstract handler with a concrete subclass would list the base as the handler.
  put src/Modules/B/B/Application/BaseThingHandler.cs 'internal abstract class BaseThingHandler : IIntegrationEventHandler<ThingHappenedIntegrationEvent>
{
}'
  put src/Modules/C/C/Application/OnThingConcrete.cs 'internal sealed class OnThingConcrete : BaseThingHandler { }'
  emit_fails "events_loud: a subclass of a handler class" "handler base class BaseThingHandler (src/Modules/B/B/Application/BaseThingHandler.cs) is derived from by OnThingConcrete (src/Modules/C/C/Application/OnThingConcrete.cs)" emit_events
  rm -f "$tree/src/Modules/C/C/Application/OnThingConcrete.cs"
  emit_fails "events_loud: an abstract handler class" "handler base class BaseThingHandler in src/Modules/B/B/Application/BaseThingHandler.cs is abstract" emit_events
}

# jobs_fixture: the plumbing of the tree (IRecurringJob, the abstract BatchedCleanupJob, the AddRecurringJob declaration and
# AddRecurringJobScheduler, the generic outbox and inbox cleanup jobs with Name => ContextName + "Suffix" and a raw string holding a fake
# Name, and the AddOutbox and AddInbox extensions that register them, one over two lines); module A registers RefreshJob (Name => JobName,
# a const of the file, a decoy Name in a string) and KeysJob (CRLF, Name => "literal", a chained call on the next line) and calls
# AddOutbox<AxDbContext>; module B calls AddInbox<BxDbContext>; decoys in comments, a /* */ block and a string; a job under tests/.
jobs_fixture() {
  local ic='src/BuildingBlocks/TemplateName.Infrastructure.Common'
  put "$ic/Jobs/IRecurringJob.cs" '/// <summary>Registered with <c>AddRecurringJob</c>; for example <c>class GhostJob : IRecurringJob</c>.</summary>
public interface IRecurringJob
{
    string Name { get; }
}'
  put "$ic/Jobs/BatchedCleanupJob.cs" 'public abstract class BatchedCleanupJob(
    DbContext context,
    TimeProvider timeProvider) : IRecurringJob
{
    public abstract string Name { get; }

    protected static string ContextNameOf(Type contextType) => contextType.Name;
}'
  put "$ic/Jobs/JobServiceCollectionExtensions.cs" 'public static class JobServiceCollectionExtensions
{
    /// <summary>Registers a job: <c>services.AddRecurringJob&lt;GhostJob&gt;()</c>.</summary>
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services)
        where TJob : class, IRecurringJob
    {
        services.AddRecurringJobScheduler();
        return services;
    }

    internal static IServiceCollection AddRecurringJobScheduler(this IServiceCollection services) => services;
}'
  put "$ic/Outbox/OutboxCleanupJob.cs" 'internal sealed class OutboxCleanupJob<TContext>(
    TContext context,
    TimeProvider timeProvider)
    : BatchedCleanupJob(context, timeProvider)
    where TContext : DbContext
{
    private const string DeleteSql = """
        DELETE TOP (@Batch) FROM {outbox} WHERE Name = "x" AND Note = '\''public override string Name => "Fake";'\''
        """;

    private static readonly string ContextName = ContextNameOf(typeof(TContext));

    public override string Name => ContextName + "OutboxCleanup";
}'
  put "$ic/Inbox/InboxCleanupJob.cs" 'internal sealed class InboxCleanupJob<TContext>(TContext context, TimeProvider timeProvider)
    : BatchedCleanupJob(context, timeProvider) where TContext : DbContext
{
    private static readonly string ContextName = ContextNameOf(typeof(TContext));

    public override string Name => ContextName + "InboxCleanup";
}'
  put "$ic/Outbox/OutboxServiceCollectionExtensions.cs" 'public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services, Assembly domainEventsAssembly)
        where TContext : DbContext
    {
        services.AddSingleton<OutboxDispatcher<TContext>>();
        services.AddRecurringJob<OutboxCleanupJob<TContext>>();
        return services;
    }
}'
  put "$ic/Inbox/InboxServiceCollectionExtensions.cs" 'public static class InboxServiceCollectionExtensions
{
    /// <summary>Call it as <c>services.AddInbox&lt;GhostDbContext&gt;()</c>.</summary>
    public static IServiceCollection AddInbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddRecurringJob<
            InboxCleanupJob<TContext>>();
        return services;
    }
}'
  put src/Modules/A/A/AModule.cs 'public static class AModule
{
    public static IServiceCollection AddA(this IServiceCollection services)
    {
        services.AddOutbox<AxDbContext>(typeof(AModule).Assembly);
        // services.AddRecurringJob<GhostJob>();
        /* services.AddInbox<GhostDbContext>(); */
        var note = "services.AddRecurringJob<GhostJob>();";
        services.AddRecurringJob<RefreshJob>();
        services
            .AddRecurringJob<KeysJob>();
        return services;
    }
}'
  put src/Modules/A/A/Infrastructure/RefreshJob.cs 'internal sealed class RefreshJob(
    ADbContext context,
    TimeProvider timeProvider)
    : BatchedCleanupJob(context, timeProvider)
{
    internal const string JobName = "ARefreshCleanup";

    private const string Note = "public override string Name => \"Fake\";";

    public override string Name => JobName;
}'
  put_raw src/Modules/A/A/Infrastructure/KeysJob.cs 'internal sealed class KeysJob(ADbContext context, TimeProvider timeProvider) : BatchedCleanupJob(context, timeProvider)\r\n{\r\n    public override string Name => "AKeysCleanup";\r\n}\r\n'
  put src/Modules/B/B/BModule.cs 'public static class BModule
{
    public static IServiceCollection AddB(this IServiceCollection services)
    {
        services.AddInbox<BxDbContext>();
        return services;
    }
}'
  put tests/A.Tests/FakeJob.cs 'internal sealed class FakeJob : IRecurringJob { public string Name => GetType().Name; }'
}

jobs_rows='AKeysCleanup|A|Jobs:AKeysCleanup:Enabled, Jobs:AKeysCleanup:Interval|src/Modules/A/A/Infrastructure/KeysJob.cs
ARefreshCleanup|A|Jobs:ARefreshCleanup:Enabled, Jobs:ARefreshCleanup:Interval|src/Modules/A/A/Infrastructure/RefreshJob.cs
AxOutboxCleanup|A|Jobs:AxOutboxCleanup:Enabled, Jobs:AxOutboxCleanup:Interval|src/BuildingBlocks/TemplateName.Infrastructure.Common/Outbox/OutboxCleanupJob.cs
BxInboxCleanup|B|Jobs:BxInboxCleanup:Enabled, Jobs:BxInboxCleanup:Interval|src/BuildingBlocks/TemplateName.Infrastructure.Common/Inbox/InboxCleanupJob.cs'

case_jobs() {
  make_tree
  jobs_fixture
  emit_of_tree emit_jobs
  assert_eq "0" "$status" "jobs: exit code"
  assert_eq "$jobs_rows"$'\n--' "$combined" "jobs: exact rows (JobName const, literal, outbox and inbox per context, tests/ ignored)"
  # The one end-to-end run of the jobs shard: the rendered table.
  generate
  assert_contains $'Rows: 4\n\n| job | module | config keys | file |\n| --- | --- | --- | --- |\n| AKeysCleanup | A | Jobs:AKeysCleanup:Enabled, Jobs:AKeysCleanup:Interval | src/Modules/A/A/Infrastructure/KeysJob.cs |' \
    "$(<"$idx/jobs.md")" "jobs: rendered shard"
  # A job of a building block registered there, a second context on the same extension, a job deriving from a job class two levels down.
  put src/BuildingBlocks/TemplateName.Infrastructure.Common/InfrastructureServiceCollectionExtensions.cs 'public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureCommon(this IServiceCollection services)
    {
        services.AddRecurringJob<IdempotencyCleanupJob>();
        return services;
    }
}'
  put src/BuildingBlocks/TemplateName.Infrastructure.Common/Idempotency/IdempotencyCleanupJob.cs 'internal sealed class IdempotencyCleanupJob(DbContext context, TimeProvider timeProvider)
    : BatchedCleanupJob(context, timeProvider)
{
    public override string Name => "IdempotencyKeysCleanup";
}'
  put src/Modules/C/C/CModule.cs 'public static class CModule
{
    public static IServiceCollection AddC(this IServiceCollection services)
    {
        services.AddOutbox<CDbContext>(typeof(CModule).Assembly);
        services.AddRecurringJob<DeepJob>();
        return services;
    }
}'
  put src/Modules/C/C/Infrastructure/DeepJob.cs 'internal abstract class MiddleJob(DbContext context) : BatchedCleanupJob(context, TimeProvider.System)
{
}

internal sealed class DeepJob(CDbContext context) : MiddleJob(context)
{
    public override string Name => "CDeepCleanup";
}'
  emit_of_tree emit_jobs
  assert_eq "$jobs_rows"$'\nCDeepCleanup|C|Jobs:CDeepCleanup:Enabled, Jobs:CDeepCleanup:Interval|src/Modules/C/C/Infrastructure/DeepJob.cs\nCOutboxCleanup|C|Jobs:COutboxCleanup:Enabled, Jobs:COutboxCleanup:Interval|src/BuildingBlocks/TemplateName.Infrastructure.Common/Outbox/OutboxCleanupJob.cs\nIdempotencyKeysCleanup|Infrastructure.Common|Jobs:IdempotencyKeysCleanup:Enabled, Jobs:IdempotencyKeysCleanup:Interval|src/BuildingBlocks/TemplateName.Infrastructure.Common/Idempotency/IdempotencyCleanupJob.cs\n--' \
    "$combined" "jobs: building-block job, second context, a job two levels down"
  # A tree without any job has no rows and no error.
  make_tree
  put src/Modules/A/A/Application/Other.cs 'class Other {}'
  emit_of_tree emit_jobs
  assert_eq "0" "$status" "jobs: no job: exit code"
  assert_eq "--" "$combined" "jobs: no job: no rows"
}

case_jobs_zero() {
  make_tree
  put src/Modules/A/A/AModule.cs 'public static class AModule
{
    public static IServiceCollection AddA(this IServiceCollection services) => services.AddRecurringJob<OnlyJob>();
}'
  put src/Modules/A/A/Infrastructure/OnlyJob.cs 'internal sealed class OnlyJob : IRecurringJob
{
    public string Name => GetType().Name;
}'
  emit_fails "jobs_zero: a job whose name the parser cannot read" "src/Modules/A/A/Infrastructure/OnlyJob.cs" emit_jobs
  # The CLI path: exit 2.
  run_gen --root "$tree" --out "$idx"
  assert_eq "2" "$status" "jobs_zero: generator exit code"
  assert_contains "src/Modules/A/A/Infrastructure/OnlyJob.cs" "$err" "jobs_zero: generator names the file"
}

# Shapes that would give a wrong or missing job row with exit 0 fail loudly instead.
case_jobs_loud() {
  local m='src/Modules/A/A/AModule.cs' r='src/Modules/A/A/Infrastructure/RefreshJob.cs' k='src/Modules/A/A/Infrastructure/KeysJob.cs'
  local ob='src/BuildingBlocks/TemplateName.Infrastructure.Common/Outbox/OutboxServiceCollectionExtensions.cs'
  # A Name the parser cannot resolve.
  make_tree
  jobs_fixture
  put "$r" 'internal sealed class RefreshJob(ADbContext context) : BatchedCleanupJob(context, TimeProvider.System)
{
    public override string Name => nameof(RefreshJob);
}'
  emit_fails "jobs_loud: an unresolved Name" "cannot read the Name of RefreshJob at line 3 of $r" emit_jobs
  # A constant of another class, and a Name set with an initializer.
  put "$r" 'internal sealed class RefreshJob(ADbContext context) : BatchedCleanupJob(context, TimeProvider.System)
{
    public override string Name => Names.Refresh;
}'
  emit_fails "jobs_loud: a constant of another class" "$r" emit_jobs
  put "$r" 'internal sealed class RefreshJob(ADbContext context) : BatchedCleanupJob(context, TimeProvider.System)
{
    public override string Name { get; } = "ARefreshCleanup";
}'
  emit_fails "jobs_loud: a Name initializer" "$r" emit_jobs
  put "$r" 'internal sealed class RefreshJob(ADbContext context) : BatchedCleanupJob(context, TimeProvider.System)
{
    public override string Name => JobName;
}'
  emit_fails "jobs_loud: a JobName constant the file does not declare" "$r" emit_jobs
  # Two jobs with the same name.
  jobs_fixture
  put_raw "$k" 'internal sealed class KeysJob(ADbContext context) : BatchedCleanupJob(context, TimeProvider.System)\r\n{\r\n    public override string Name => "ARefreshCleanup";\r\n}\r\n'
  emit_fails "jobs_loud: two jobs with one name" "two jobs named ARefreshCleanup" emit_jobs
  # A job class that nothing registers (the source without the wiring).
  jobs_fixture
  put src/Modules/A/A/Infrastructure/OrphanJob.cs 'internal sealed class OrphanJob(ADbContext context)
    : BatchedCleanupJob(context, TimeProvider.System)
{
    public override string Name => "AOrphanCleanup";
}'
  emit_fails "jobs_loud: an unregistered job class" "src/Modules/A/A/Infrastructure/OrphanJob.cs" emit_jobs
  rm -f "$tree/src/Modules/A/A/Infrastructure/OrphanJob.cs"
  # A registration of a class that no file declares (the wiring without the source).
  put "$m" 'public static class AModule
{
    public static IServiceCollection AddA(this IServiceCollection services)
    {
        services.AddRecurringJob<RefreshJob>();
        services.AddRecurringJob<KeysJob>();
        services.AddRecurringJob<MissingJob>();
        return services;
    }
}'
  emit_fails "jobs_loud: a registration of an unknown class" "AddRecurringJob<MissingJob> at line 7 of $m" emit_jobs
  # A registration shape the parser does not know.
  put "$m" 'public static class AModule
{
    public static IServiceCollection AddA(this IServiceCollection services)
    {
        services.AddRecurringJob<KeysJob>();
        services.AddRecurringJob(typeof(RefreshJob));
        return services;
    }
}'
  emit_fails "jobs_loud: an unknown registration shape" "line 6 of $m" emit_jobs
  # A generic job registered outside an extension method with a type parameter.
  put "$m" 'public static class AModule
{
    public static IServiceCollection AddA(this IServiceCollection services)
    {
        services.AddRecurringJob<RefreshJob>();
        services.AddRecurringJob<KeysJob>();
        services.AddRecurringJob<OutboxCleanupJob<AxDbContext>>();
        return services;
    }
}'
  emit_fails "jobs_loud: a generic job registered directly" "line 7 of $m" emit_jobs
  # A context whose name does not end in DbContext.
  jobs_fixture
  put src/Modules/B/B/BModule.cs 'public static class BModule
{
    public static IServiceCollection AddB(this IServiceCollection services) => services.AddInbox<BxContext>();
}'
  emit_fails "jobs_loud: a context not ending in DbContext" "src/Modules/B/B/BModule.cs" emit_jobs
  # AddOutbox called but the extension that registers its job is not found.
  jobs_fixture
  rm -f "$tree/$ob"
  emit_fails "jobs_loud: AddOutbox without its extension" "AddOutbox<...> is called in $m" emit_jobs
}

# config_fixture: a Host appsettings.json in CRLF (JSON with comments) with Serilog, Auth (wrapped in //#if marker lines), Caching and a
# commented-out section; module A owns Auth (`Auth:Lockout`, two files), two building blocks own Caching (`Caching` and `Caching:Redis`),
# a SectionName of a section the file does not have (not listed), decoys (a use of SectionName in an interpolated string and in a
# concatenation, a SectionName under tests/, another appsettings file with an unowned section); docs: a heading `Auth` in a module doc, a
# table row `Serilog` in api.md, Caching only in prose and in a fenced code block.
config_fixture() {
  put_raw src/Host/H/appsettings.json '{\r\n  "Serilog": {\r\n    "MinimumLevel": "Information"\r\n  },\r\n  // "Old": { "Gone": true },\r\n  /* "Older": {} */\r\n  //#if (notifications)\r\n  "Auth": {\r\n    "Lockout": { "Max": 5 },\r\n    "Text": "a \\"quoted\\" { brace"\r\n  },\r\n  //#endif\r\n  "Caching": {\r\n    //#if (caching == "redis")\r\n    "Redis": { "InstanceName": "x" },\r\n    //#endif\r\n    "Provider": "memory"\r\n  }\r\n}\r\n'
  put src/Host/H/appsettings.Development.json '{
  "Unowned": {}
}'
  put tests/T.IntegrationTests/appsettings.json '{
  "AlsoUnowned": {}
}'
  put src/Modules/A/A/Application/AuthOptions.cs 'internal sealed class LockoutOptions
{
    internal const string SectionName = "Auth:Lockout";

    public string Path => $"{SectionName}:Max";
}'
  put src/Modules/A/A/Application/SeedOptions.cs 'internal sealed class SeedOptions
{
    internal const string SectionName = "Auth:Seed";
    internal const string AdminPath = SectionName + ":" + nameof(Admin);
}'
  put src/Modules/A/A/Infrastructure/RetentionOptions.cs 'internal sealed class RetentionOptions
{
    internal const string SectionName = "Retention";
}'
  put src/BuildingBlocks/TemplateName.Infrastructure.Common/Caching/CachingOptions.cs 'public sealed class CachingOptions
{
    public const string SectionName = "Caching";
}'
  put src/BuildingBlocks/TemplateName.Platform.Redis/RedisCachingOptions.cs '// The SectionName below is read with GetSection(RedisCachingOptions.SectionName).
internal sealed class RedisCachingOptions
{
    internal const string SectionName = "Caching:Redis";
}'
  put tests/T.UnitTests/A/FakeOptions.cs 'internal sealed class FakeOptions { internal const string SectionName = "Mystery"; }'
  put docs/modules/a.md '# A

## Configuration

### `Auth`

Uses `Caching` too.'
  put docs/services/api.md '# API

| Key | Default |
|---|---|
| `Serilog` | see below |

```
### `Caching`
```'
  put docs/other/x.md '### `Caching`'
}

config_rows='Auth|A|docs/modules/a.md
Caching|Infrastructure.Common, Platform.Redis|-
Serilog|Host|docs/services/api.md'

case_config() {
  make_tree
  config_fixture
  emit_of_tree emit_config
  assert_eq "0" "$status" "config: exit code"
  assert_eq "$config_rows"$'\n--' "$combined" "config: exact rows (markers skipped, owners joined, Host allowlist, docs rule, CRLF)"
  # The one end-to-end run of the config shard: the rendered table.
  generate
  assert_contains $'Rows: 3\n\n| section | owner | documented in |\n| --- | --- | --- |\n| Auth | A | docs/modules/a.md |' \
    "$(<"$idx/config.md")" "config: rendered shard"
  # A section owned by the Host and a project at once lists both; a heading with a sub-key and a table row with one count.
  put src/Host/H/SerilogOptions.cs 'internal sealed class SerilogOptions { internal const string SectionName = "Serilog"; }'
  put docs/building-blocks/b.md '| `Caching:Provider` | memory |'
  put docs/modules/c.md '### `Auth:Seed`'
  emit_of_tree emit_config
  assert_eq $'Auth|A|docs/modules/a.md, docs/modules/c.md\nCaching|Infrastructure.Common, Platform.Redis|docs/building-blocks/b.md\nSerilog|Host|docs/services/api.md\n--' \
    "$combined" "config: Host project owner, sub-key heading and table row"
  # A tree without a Host appsettings.json has no rows and no error.
  make_tree
  put src/Modules/A/A/AuthOptions.cs 'internal sealed class LockoutOptions { internal const string SectionName = "Auth:Lockout"; }'
  emit_of_tree emit_config
  assert_eq "0" "$status" "config: no appsettings: exit code"
  assert_eq "--" "$combined" "config: no appsettings: no rows"
}

# Shapes that would give a wrong or missing configuration row with exit 0 fail loudly instead.
case_config_loud() {
  local app='src/Host/H/appsettings.json' opt='src/Modules/A/A/Application/AuthOptions.cs'
  # A section with no owner and not in the Host allowlist.
  make_tree
  config_fixture
  put "$app" '{
  "Serilog": {},
  "Auth": {},
  "Caching": {},
  "Mystery": { "X": 1 }
}'
  emit_fails "config_loud: an unassigned section" "Mystery" emit_config
  run_gen --root "$tree" --out "$idx"
  assert_eq "2" "$status" "config_loud: generator exit code"
  assert_contains "Mystery" "$err" "config_loud: generator names the section"
  # A section that appears twice.
  put "$app" '{
  "Serilog": {},
  "Auth": {},
  "Auth": {}
}'
  emit_fails "config_loud: a section twice" "Auth" emit_config
  # A section not at the two-space indent, and a key that is not letters only.
  put "$app" '{
    "Serilog": {},
    "Auth": {}
}'
  emit_fails "config_loud: four-space indent" "line 2 of $app" emit_config
  put "$app" '{
  "Serilog": {}, "Auth": {}
}'
  emit_fails "config_loud: two sections on one line" "line 2 of $app" emit_config
  put "$app" '{
  "Serilog": {},
  "Auth_X": {}
}'
  emit_fails "config_loud: a section name the regex does not take" "line 3 of $app" emit_config
  # A marker that is not a whole line, and a file that does not close.
  put "$app" '{
  "Serilog": {}, //#if (notifications)
  "Auth": {}
}'
  emit_fails "config_loud: a marker inside a line" "line 2 of $app" emit_config
  put "$app" '{
  "Serilog": {},
  "Auth": {'
  emit_fails "config_loud: an unclosed file" "$app" emit_config
  put "$app" '{
}'
  emit_fails "config_loud: no section" "$app" emit_config
  # SectionName shapes the parser cannot read.
  config_fixture
  put "$opt" 'internal sealed class LockoutOptions { internal const string SectionName = "Auth" + ":Lockout"; }'
  emit_fails "config_loud: a concatenated SectionName" "line 1 of $opt" emit_config
  put "$opt" 'internal sealed class LockoutOptions { internal const string SectionName = $"Auth:{Name}"; }'
  emit_fails "config_loud: an interpolated SectionName" "line 1 of $opt" emit_config
  put "$opt" 'internal sealed class LockoutOptions
{
    internal static readonly string SectionName = "Auth:Lockout";
}'
  emit_fails "config_loud: a static readonly SectionName" "line 3 of $opt" emit_config
  put "$opt" 'internal sealed class LockoutOptions { public static string SectionName => "Auth:Lockout"; }'
  emit_fails "config_loud: a SectionName property" "line 1 of $opt" emit_config
  put "$opt" 'internal sealed class LockoutOptions { internal const string SectionName = "Auth.Lockout"; }'
  emit_fails "config_loud: a SectionName that is not a configuration path" "line 1 of $opt" emit_config
}

# tests_fixture: two test projects: T.UnitTests with module and building-block folders (nested files counted, a non-.cs file and a
# bin folder not counted), a cross-cutting folder, a folder that matches a module and a building block (the module wins) and a loose
# file at its root; T.ArchitectureTests with no folder. Sources: a module, building blocks, pairs and decoys.
tests_fixture() {
  put src/Modules/Auth/Auth/Application/Login/Login.cs 'class Login {}'
  put src/Modules/Redis/Redis/RedisModule.cs 'class RedisModule {}'
  put src/BuildingBlocks/TemplateName.Web.Common/Http/WebThing.cs 'class WebThing {}'
  put src/BuildingBlocks/TemplateName.Platform.Redis/RedisCache.cs 'class RedisCache {}'
  put tests/T.UnitTests/Auth/Login/LoginTests.cs 'class LoginTests {}'
  put tests/T.UnitTests/Auth/Helpers.cs 'class Helpers {}'
  put tests/T.UnitTests/Auth/data.json '{}'
  put tests/T.UnitTests/Web/WebThingTests.cs 'class WebThingTests {}'
  put tests/T.UnitTests/Odd/OddTests.cs 'class OddTests {}'
  put tests/T.UnitTests/Redis/RedisCacheTests.cs 'class RedisCacheTests {}'
  put tests/T.UnitTests/Usings.cs 'global using Xunit;'
  put tests/T.UnitTests/bin/Debug/Gen.cs 'class Gen {}'
  put tests/T.UnitTests/T.UnitTests.csproj '<Project />'
  put tests/T.ArchitectureTests/LayerTests.cs 'class LayerTests {}'
  put tests/T.ArchitectureTests/NamingTests.cs 'class NamingTests {}'
}

tests_rows='T.ArchitectureTests|(flat)|-|2
T.UnitTests|(root)|-|1
T.UnitTests|Auth|Auth|2
T.UnitTests|Odd|cross-cutting|1
T.UnitTests|Redis|Redis|1
T.UnitTests|Web|Web.Common|1'

case_tests_folders() {
  make_tree
  tests_fixture
  emit_of_tree emit_tests
  assert_eq "0" "$status" "tests_folders: exit code"
  assert_eq "$tests_rows"$'\n--' "$combined" "tests_folders: exact rows (module, building block, cross-cutting, module wins, root, flat, .cs only)"
  # A tree without tests has no rows and no error.
  make_tree
  put src/Modules/Auth/Auth/A.cs 'class A {}'
  emit_of_tree emit_tests
  assert_eq "0" "$status" "tests_folders: no tests: exit code"
  assert_eq "--" "$combined" "tests_folders: no tests: no rows"
}

# tests_pairs: runs emit_tests_2 on the current tree; sets status and combined (the rows, sorted, then "note: " and the table note).
tests_pairs() {
  sourced "root='$tree'; cd '$tree'; prepare_files; emit_tests_2 > \"\$work/p\"; sort \"\$work/p\"; echo \"note: \$table_note\""
}

case_tests_pairs() {
  make_tree
  tests_fixture
  # Dup.cs twice in src/ and BarTests.cs twice in tests/: skipped, two names counted; Foo.cs under Migrations does not make Foo
  # ambiguous, and Snapshot.cs under Migrations does not pair.
  put src/Modules/Auth/Auth/Application/Foo.cs 'class Foo {}'
  put src/Modules/Auth/Auth/Infrastructure/Persistence/Migrations/Foo.cs 'class Foo {}'
  put src/Modules/Auth/Auth/Infrastructure/Persistence/Migrations/Snapshot.cs 'class Snapshot {}'
  put src/Modules/Auth/Auth/Application/Dup.cs 'class Dup {}'
  put src/Modules/Redis/Redis/Dup.cs 'class Dup {}'
  put src/Modules/Auth/Auth/Application/Bar.cs 'class Bar {}'
  put tests/T.UnitTests/Auth/FooTests.cs 'class FooTests {}'
  put tests/T.UnitTests/Auth/SnapshotTests.cs 'class SnapshotTests {}'
  put tests/T.UnitTests/Auth/DupTests.cs 'class DupTests {}'
  put tests/T.UnitTests/Auth/BarTests.cs 'class BarTests {}'
  put tests/T.ArchitectureTests/Auth/BarTests.cs 'class BarTests {}'
  tests_pairs
  assert_eq "0" "$status" "tests_pairs: exit code"
  assert_eq 'src/BuildingBlocks/TemplateName.Platform.Redis/RedisCache.cs|tests/T.UnitTests/Redis/RedisCacheTests.cs
src/BuildingBlocks/TemplateName.Web.Common/Http/WebThing.cs|tests/T.UnitTests/Web/WebThingTests.cs
src/Modules/Auth/Auth/Application/Foo.cs|tests/T.UnitTests/Auth/FooTests.cs
src/Modules/Auth/Auth/Application/Login/Login.cs|tests/T.UnitTests/Auth/Login/LoginTests.cs' "$(printf '%s\n' "$combined" | sed '$d')" \
    "tests_pairs: exact pairs (unique names only, Migrations ignored)"
  assert_contains "2 names that occur more than once" "$(printf '%s\n' "$combined" | tail -n 1)" "tests_pairs: the note counts the skipped names"
  # The one end-to-end run of the tests shard: both tables, the note above the second, Rows over both.
  generate
  assert_contains $'Rows: 11\n\n## Folders\n\n| test project | folder | source area | files |' "$(<"$idx/tests.md")" "tests_pairs: rendered Rows over both tables"
  assert_contains $'## Name pairs\n\nPairs `Foo.cs`' "$(<"$idx/tests.md")" "tests_pairs: rendered note above the pairs"
}

# Shapes that would give a wrong test row with exit 0 fail loudly instead.
case_tests_loud() {
  # A folder that names two building blocks.
  make_tree
  tests_fixture
  put src/BuildingBlocks/TemplateName.Application.Common/A.cs 'class A {}'
  put src/BuildingBlocks/TemplateName.Infrastructure.Common/B.cs 'class B {}'
  put tests/T.UnitTests/Common/CommonTests.cs 'class CommonTests {}'
  emit_fails "tests_loud: a folder matching two building blocks" "tests/T.UnitTests/Common" emit_tests
  # A .cs file directly under tests/, outside any test project.
  make_tree
  tests_fixture
  put tests/Stray.cs 'class Stray {}'
  emit_fails "tests_loud: a .cs file outside a test project" "tests/Stray.cs" emit_tests
}

# Each case runs in a subshell so that its tree and variables do not leak into the next.
# The subshell reports its failures through its exit code, since variables do not cross the boundary.
run_case() {
  local name="$1" rc=0
  ( failures=0; "$name"; exit $(( failures > 0 ? 1 : 0 )) ) || rc=$?
  if (( rc != 0 )); then failures=$((failures + 1)); fi
}

all_cases="case_usage_errors case_markers_shard case_markers_kinds_and_expressions case_markers_zero_rows case_stub_shards
  case_two_table_shard case_atomic_write
  case_determinism case_untracked_ignored case_git_file_marker case_find_excludes_build_dirs
  case_check_stale case_check_missing_and_unknown case_map_rules case_need_rows
  case_routes case_routes_zero case_routes_module_absent case_routes_loud case_routes_shapes
  case_permissions case_permissions_loud case_permissions_shapes case_errors case_errors_zero case_errors_loud
  case_events case_events_zero case_events_loud case_jobs case_jobs_zero case_jobs_loud
  case_config case_config_loud case_tests_folders case_tests_pairs case_tests_loud case_unknown_case_name"

# With arguments, only the named cases run (for iterating on one case); without, all of them. Every argument is checked against the
# registered list before anything runs: run_case ignores set -e, so a misspelled name would otherwise print "command not found" and pass.
registered=" ${all_cases//$'\n'/ } "
for c in "$@"; do
  if [[ -z "$c" || "$registered" != *" $c "* ]]; then
    echo "project-index: unknown case: $c" >&2
    exit 2
  fi
done
for c in ${*:-$all_cases}; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "project-index: $failures failure(s)" >&2
  exit 1
fi
echo "project-index: passed"
