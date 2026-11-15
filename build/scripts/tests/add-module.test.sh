#!/usr/bin/env bash
# Test for build/scripts/add-module.sh (input checks, preflight, cleanup and the documentation rename). It builds a fake generated
# project (the repo's anchor-bearing files with TemplateName replaced by Acme.Shop, as the engine would, plus a stand-in Audit module)
# and a simulated engine output of the item template for a given name and prefix, then runs or sources the script against it.
#
# Usage: bash build/scripts/tests/add-module.test.sh   (from any directory)
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
script="$repo_root/build/scripts/add-module.sh"
anchors="$repo_root/build/scripts/module-anchors.txt"
template="$repo_root/templates/modulith-module"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

if [[ ! -f "$script" ]]; then
  echo "FAIL: $script does not exist" >&2
  exit 1
fi

tmp="$(mktemp -d)"
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT

# Forking is slow on some machines (Windows with Git Bash: about half a second per process), so the helpers below use shell built-ins
# for the per-file work, build the fake project once and copy it per scenario, and checksum a whole tree with one cksum call.

base_template="$tmp/base-template"

# build_base DIR: the fake generated project (prefix Acme.Shop) before the engine writes a module. Built once, then copied.
build_base() {
  local dir="$1" key relative anchor path target content
  if [[ ! -d "$base_template" ]]; then
    local paths=("build/scripts/module-anchors.txt")
    while IFS='|' read -r key relative anchor || [[ -n "$key" ]]; do
      [[ -z "$key" ]] && continue
      paths+=("$relative")
    done < "$anchors"
    local -a directories=()
    for path in "${paths[@]}"; do
      target="${path//TemplateName/Acme.Shop}"
      if [[ "$target" == */* ]]; then
        directories+=("$base_template/${target%/*}")
      fi
    done
    # A real module that is already wired: Program.cs registers it (AddAuditModule) and its files exist.
    directories+=("$base_template/src/Modules/Audit/Acme.Shop.Modules.Audit" "$base_template/docs/modules")
    mkdir -p "${directories[@]}"
    for path in "${paths[@]}"; do
      target="${path//TemplateName/Acme.Shop}"
      content="$(< "$repo_root/$path")"
      printf '%s\n' "${content//TemplateName/Acme.Shop}" > "$base_template/$target"
    done
    echo "// the Audit module" > "$base_template/src/Modules/Audit/Acme.Shop.Modules.Audit/AuditModule.cs"
    echo "# Audit" > "$base_template/docs/modules/audit.md"
  fi
  cp -R "$base_template" "$dir"
}

# The template tree without .template.config, read once.
template_files=()
while IFS= read -r path; do
  template_files+=("${path#"$template"/}")
done < <(find "$template" -type f ! -path "$template/.template.config/*" | sort)

# simulate_engine DIR NAME PREFIX: what `dotnet new modulith-module` leaves behind: the template tree (without .template.config) with
# ModuleName and ProjectPrefix replaced in names and content, and the marker.
simulate_engine() {
  local dir="$1" name="$2" prefix="$3" relative target content
  local -a directories=("$dir")
  for relative in "${template_files[@]}"; do
    target="${relative//ModuleName/$name}"
    target="${target//ProjectPrefix/$prefix}"
    if [[ "$target" == */* ]]; then
      directories+=("$dir/${target%/*}")
    fi
  done
  mkdir -p "${directories[@]}"
  for relative in "${template_files[@]}"; do
    target="${relative//ModuleName/$name}"
    target="${target//ProjectPrefix/$prefix}"
    content="$(< "$template/$relative")"
    content="${content//ModuleName/$name}"
    printf '%s\n' "${content//ProjectPrefix/$prefix}" > "$dir/$target"
  done
}

# snapshot DIR: one line per file, "checksum size path", sorted; equal snapshots mean a byte-identical tree.
snapshot() {
  (cd "$1" && find . -type f -exec cksum {} + | sort -k3)
}

# run_in DIR ARGS...: runs the script from DIR; prints the exit code, output goes to $tmp/out.log.
run_in() {
  local dir="$1"
  shift
  local code=0
  (cd "$dir" && bash "$script" "$@") > "$tmp/out.log" 2>&1 || code=$?
  echo "$code"
}

assert_code() {
  local label="$1" expected="$2" actual="$3"
  if [[ "$actual" != "$expected" ]]; then
    fail "$label: exit code $actual, expected $expected ($(tr '\n' ' ' < "$tmp/out.log"))"
  fi
}

assert_same_tree() {
  local label="$1" before="$2" dir="$3"
  if [[ "$before" != "$(snapshot "$dir")" ]]; then
    fail "$label: the tree changed"
    diff <(echo "$before") <(snapshot "$dir") >&2 || true
  fi
}

# has_entry DIR NAME: true when DIR lists NAME with exactly that spelling (a case-insensitive file system would answer -f for any case).
has_entry() {
  ls -1 "$1" | grep -qxF -- "$2"
}

# --- kebab_case, validate_name, remove_generated called directly ---------------------------------------------------------------

# shellcheck disable=SC1090
source_script() { source "$script"; }

for pair in LeaveManagement=leave-management HRPayroll=h-r-payroll Billing2=billing2 Audit=audit Application.Common=application-common; do
  input="${pair%%=*}"
  expected="${pair#*=}"
  actual="$( (source_script; kebab_case "$input") )"
  [[ "$actual" == "$expected" ]] || fail "kebab_case $input gave '$actual', expected '$expected'"
done

for valid in Billing Billing2 HRPayroll; do
  code=0
  (source_script; validate_name "$valid") > /dev/null 2>&1 || code=$?
  [[ "$code" == 0 ]] || fail "validate_name rejected '$valid' (exit $code)"
done
for invalid in billing Bill_ing 2Billing "" "Bill ing" "Bill-ing" "../Billing"; do
  code=0
  (source_script; validate_name "$invalid") > /dev/null 2>&1 || code=$?
  [[ "$code" == 2 ]] || fail "validate_name '$invalid' exited $code, expected 2"
done

# remove_generated removes the module, its pages and the marker, and nothing else.
base="$tmp/direct"
build_base "$base"
before="$(snapshot "$base")"
simulate_engine "$base" Billing Acme.Shop
echo "stray" > "$base/docs/modules/billing.md"
(cd "$base" && source_script && remove_generated Billing Acme.Shop) > /dev/null 2>&1 || fail "remove_generated Billing failed"
assert_same_tree "remove_generated" "$before" "$base"

# It leaves a module alone that Program.cs already registers, and a name that points outside the module folders.
base="$tmp/direct-audit"
build_base "$base"
before="$(snapshot "$base")"
for prefix in Acme.Shop Wrong.Name; do
  code=0
  (cd "$base" && source_script && remove_generated Audit "$prefix") > /dev/null 2>&1 || code=$?
  [[ "$code" != 0 ]] || fail "remove_generated Audit $prefix returned 0, expected non-zero (it must refuse)"
  assert_same_tree "remove_generated Audit $prefix" "$before" "$base"
done
for hostile in ".." "../Modules" "a/b"; do
  (cd "$base" && source_script && remove_generated "$hostile" Acme.Shop) > /dev/null 2>&1 || fail "remove_generated '$hostile' failed"
  assert_same_tree "remove_generated '$hostile'" "$before" "$base"
done

# --- invalid names: exit 2, the generated module is gone, no other file changed ------------------------------------------------------

for invalid in billing Bill_ing 2Billing; do
  base="$tmp/invalid-$invalid"
  build_base "$base"
  before="$(snapshot "$base")"
  simulate_engine "$base" "$invalid" Acme.Shop
  code="$(run_in "$base" --name "$invalid" --prefix Acme.Shop)"
  assert_code "name '$invalid'" 2 "$code"
  [[ ! -e "$base/src/Modules/$invalid" ]] || fail "name '$invalid': src/Modules/$invalid still exists"
  assert_same_tree "name '$invalid'" "$before" "$base"
done

# --- a prefix that is not the project's: exit 2 before any edit, the module is removed, the rest is byte-identical --------------------

base="$tmp/wrong-prefix"
build_base "$base"
before="$(snapshot "$base")"
simulate_engine "$base" Billing Wrong.Name
code="$(run_in "$base")"
assert_code "prefix Wrong.Name through the marker" 2 "$code"
[[ ! -e "$base/src/Modules/Billing" ]] || fail "prefix Wrong.Name: src/Modules/Billing still exists"
[[ ! -e "$base/docs/modules/Billing.md" ]] || fail "prefix Wrong.Name: docs/modules/Billing.md still exists"
[[ ! -e "$base/.modulith-module" ]] || fail "prefix Wrong.Name: the marker still exists"
assert_same_tree "prefix Wrong.Name" "$before" "$base"

base="$tmp/wrong-prefix-flags"
build_base "$base"
before="$(snapshot "$base")"
simulate_engine "$base" Billing Wrong.Name
code="$(run_in "$base" --name Billing --prefix Wrong.Name)"
assert_code "prefix Wrong.Name through the flags" 2 "$code"
assert_same_tree "prefix Wrong.Name through the flags" "$before" "$base"

# --- a module that is already wired: exit 2 and nothing changes ---------------------------------------------------------------------

base="$tmp/wired"
build_base "$base"
printf 'Audit\nAcme.Shop\n' > "$base/.modulith-module"
before="$(snapshot "$base")"
code="$(run_in "$base" --name Audit --prefix Acme.Shop)"
assert_code "--name Audit" 2 "$code"
grep -q "already wired" "$tmp/out.log" || fail "--name Audit: the message does not say it is already wired"
assert_same_tree "--name Audit" "$before" "$base"
[[ -f "$base/.modulith-module" ]] || fail "--name Audit: the marker was deleted (it must stay)"
[[ -f "$base/src/Modules/Audit/Acme.Shop.Modules.Audit/AuditModule.cs" ]] || fail "--name Audit: the Audit module is gone"

# A wrong prefix must not hide a registered module: the host that registers it is found under any prefix, nothing is deleted.
base="$tmp/wired-wrong-prefix"
build_base "$base"
before="$(snapshot "$base")"
code="$(run_in "$base" --name Audit --prefix Wrong.Name)"
assert_code "--name Audit --prefix Wrong.Name" 2 "$code"
assert_same_tree "--name Audit --prefix Wrong.Name" "$before" "$base"
[[ -f "$base/src/Modules/Audit/Acme.Shop.Modules.Audit/AuditModule.cs" ]] || fail "--name Audit --prefix Wrong.Name: the Audit module is gone"
[[ -f "$base/docs/modules/audit.md" ]] || fail "--name Audit --prefix Wrong.Name: docs/modules/audit.md is gone"

# The same for case variants of a registered name (the same folder on a case-insensitive file system): AUDIT is a valid name,
# audit is not (the invalid-name path), and a wrong prefix does not matter.
for variant in AUDIT audit; do
  for prefix in Wrong.Name Acme.Shop; do
    base="$tmp/wired-$variant-$prefix"
    build_base "$base"
    before="$(snapshot "$base")"
    code="$(run_in "$base" --name "$variant" --prefix "$prefix")"
    assert_code "--name $variant --prefix $prefix" 2 "$code"
    assert_same_tree "--name $variant --prefix $prefix" "$before" "$base"
    [[ -f "$base/src/Modules/Audit/Acme.Shop.Modules.Audit/AuditModule.cs" ]] || fail "--name $variant --prefix $prefix: the Audit module is gone"
  done
done

# --- the happy path: the documentation page is renamed and the marker is deleted ------------------------------------------------------

for pair in LeaveManagement=leave-management HRPayroll=h-r-payroll Billing2=billing2; do
  name="${pair%%=*}"
  page="${pair#*=}.md"
  base="$tmp/happy-$name"
  build_base "$base"
  simulate_engine "$base" "$name" Acme.Shop
  code="$(run_in "$base")"
  assert_code "happy path $name" 0 "$code"
  has_entry "$base/docs/modules" "$page" || fail "happy path $name: docs/modules/$page does not exist"
  has_entry "$base/docs/modules" "$name.md" && fail "happy path $name: docs/modules/$name.md was not renamed"
  [[ ! -e "$base/.modulith-module" ]] || fail "happy path $name: the marker was not deleted"
  [[ -d "$base/src/Modules/$name" ]] || fail "happy path $name: src/Modules/$name is gone"
done

# A marker with CRLF line endings, trailing blanks and no final newline reads the same.
base="$tmp/happy-crlf"
build_base "$base"
simulate_engine "$base" Billing Acme.Shop
printf 'Billing  \r\nAcme.Shop\t\r\n' > "$base/.modulith-module"
code="$(run_in "$base")"
assert_code "CRLF marker" 0 "$code"
has_entry "$base/docs/modules" "billing.md" || fail "CRLF marker: docs/modules/billing.md does not exist"

base="$tmp/happy-no-newline"
build_base "$base"
simulate_engine "$base" Billing Acme.Shop
printf 'Billing\nAcme.Shop' > "$base/.modulith-module"
code="$(run_in "$base")"
assert_code "marker without a final newline" 0 "$code"

# A manual re-run: flags and no marker.
base="$tmp/happy-flags"
build_base "$base"
simulate_engine "$base" Billing Acme.Shop
rm "$base/.modulith-module"
code="$(run_in "$base" --name Billing --prefix Acme.Shop)"
assert_code "flags without a marker" 0 "$code"
has_entry "$base/docs/modules" "billing.md" || fail "flags without a marker: docs/modules/billing.md does not exist"

# --- other input errors --------------------------------------------------------------------------------------------------------------

base="$tmp/no-marker"
build_base "$base"
before="$(snapshot "$base")"
code="$(run_in "$base")"
assert_code "no arguments and no marker" 2 "$code"
assert_same_tree "no arguments and no marker" "$before" "$base"

code="$(run_in "$base" --name Billing)"
assert_code "--name without a prefix and no marker" 2 "$code"
code="$(run_in "$base" --bogus)"
assert_code "unknown flag" 2 "$code"
code="$(run_in "$base" --name)"
assert_code "--name without a value" 2 "$code"
assert_same_tree "input errors" "$before" "$base"

# --- the target page already exists: exit 2 and it is not overwritten ----------------------------------------------------------------

base="$tmp/page-exists"
build_base "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
echo "an existing page" > "$base/docs/modules/leave-management.md"
code="$(run_in "$base")"
assert_code "target page exists" 2 "$code"
[[ "$(cat "$base/docs/modules/leave-management.md")" == "an existing page" ]] || fail "target page exists: the existing page was overwritten"
[[ -f "$base/docs/modules/LeaveManagement.md" ]] || fail "target page exists: the generated page was moved"

# The generated page is missing: exit 1.
base="$tmp/page-missing"
build_base "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
rm "$base/docs/modules/LeaveManagement.md"
code="$(run_in "$base")"
assert_code "generated page missing" 1 "$code"

if ((failures > 0)); then
  echo "$failures failure(s)" >&2
  exit 1
fi
echo "add-module tests passed"
