#!/usr/bin/env bash
# Tests for project-index.sh (project index design): the CLI, the markers, routes, permissions and errors shards, the MAP.md checks and --check.
# Each case builds its own throwaway tree, runs the script on it and checks the files it writes, its output and its exit code.
#
# Usage: bash build/scripts/tests/project-index.test.sh [case_name...]   (no argument: every case)
set -euo pipefail

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
}

# errors_fixture: module A with the three declaration shapes (literal on the same line, literal on the next line, a const string) and
# decoys (a doc comment quoting a code, a comma and a parenthesis inside a message, an escaped quote); a building block in CRLF with a
# qualified factory call, Unavailable and Failure; neutral resx files (one with an XML comment holding a <data> example, one CRLF) and a
# culture file that is ignored.
errors_fixture() {
  put src/Modules/A/A/Domain/AErrors.cs 'internal static class AErrors
{
    /// <summary>See <see cref="Busy"/>, code "a.doc".</summary>
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
  case_permissions case_permissions_loud case_errors case_errors_zero case_errors_loud"

# With arguments, only the named cases run (for iterating on one case); without, all of them.
for c in ${*:-$all_cases}; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "project-index: $failures failure(s)" >&2
  exit 1
fi
echo "project-index: passed"
