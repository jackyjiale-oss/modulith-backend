#!/usr/bin/env bash
# Test for build/scripts/add-module.sh (input checks, preflight, cleanup, the documentation rename and the edits at the anchors). It builds a fake generated
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
export TMPDIR="$tmp" # the script's dotnet logs land in the scratch folder

# A stand-in for the dotnet executable (the script reads it from ADD_MODULE_DOTNET): it appends its arguments to $RECORD, one line per call, and
# exits 0, or 1 when FAIL_ON is set and the arguments start with it. On 'restore' it also creates the files named in LOCK_FILES (what
# the real restore does with packages.lock.json). It also prints a line to standard output and one to standard error, like a build.
# Every scenario below runs against it, so no test ever starts a real dotnet.
dotnet_stub="$tmp/dotnet-stub"
cat > "$dotnet_stub" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$RECORD"
echo "stub output of: $*"; echo "stub error output of: $*" >&2
if [[ -n "${FAIL_ON:-}" && "$*" == "$FAIL_ON"* ]]; then exit 1; fi
if [[ "${1:-}" == restore ]]; then
  for lock in ${LOCK_FILES:-}; do : > "$lock"; done
fi
exit 0
STUB
export ADD_MODULE_DOTNET="$dotnet_stub"
export RECORD="$tmp/dotnet.rec"

# Forking is slow on some machines (Windows with Git Bash: about half a second per process), so the helpers below use shell built-ins
# for the per-file work, build the fake project once and copy it per scenario, and checksum a whole tree with one cksum call.

base_template="$tmp/base-template"

# build_base DIR: the fake generated project (prefix Acme.Shop) before the engine writes a module. Built once, then copied.
build_base() {
  local dir="$1" key relative anchor path target content
  if [[ ! -d "$base_template" ]]; then
    # Every file an edit touches: the anchor files, the anchor list itself, the docs index and the commit scope list.
    local paths=("build/scripts/module-anchors.txt" "docs/README.md" "build/scripts/check-commit-msg.sh")
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

# simulate_engine DIR NAME PREFIX [CONTRACTS]: what `dotnet new modulith-module` leaves behind: the template tree (without
# .template.config) with ModuleName and ProjectPrefix replaced in names and content, and the marker; the Contracts project only when
# CONTRACTS is 1.
simulate_engine() {
  local dir="$1" name="$2" prefix="$3" contracts="${4:-0}" relative target content
  local -a directories=("$dir")
  for relative in "${template_files[@]}"; do
    # Like the engine's (!contracts) source modifier: the Contracts project is written only when asked for.
    [[ "$contracts" != 1 && "$relative" == *.Contracts/* ]] && continue
    target="${relative//ModuleName/$name}"
    target="${target//ProjectPrefix/$prefix}"
    if [[ "$target" == */* ]]; then
      directories+=("$dir/${target%/*}")
    fi
  done
  mkdir -p "${directories[@]}"
  for relative in "${template_files[@]}"; do
    [[ "$contracts" != 1 && "$relative" == *.Contracts/* ]] && continue
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

# remove_generated removes what the engine wrote (the module's two projects, the docs page named after the module, the marker) and
# nothing else: not the kebab-cased page (the engine never writes it), not a file of the module folder it did not write.
base="$tmp/direct"
build_base "$base"
echo "stray" > "$base/docs/modules/leave-management.md" # a name whose kebab page differs from the generated one by more than case
before="$(snapshot "$base")"
simulate_engine "$base" LeaveManagement Acme.Shop 1
(cd "$base" && source_script && remove_generated LeaveManagement Acme.Shop) > /dev/null 2>&1 || fail "remove_generated LeaveManagement failed"
assert_same_tree "remove_generated" "$before" "$base"
[[ ! -e "$base/src/Modules/LeaveManagement" ]] || fail "remove_generated: the empty src/Modules/LeaveManagement was left behind"

# The parent folder goes only when it is empty after the two projects are gone.
base="$tmp/direct-parent"
build_base "$base"
mkdir -p "$base/src/Modules/Billing"
echo "notes" > "$base/src/Modules/Billing/notes.txt"
before="$(snapshot "$base")"
simulate_engine "$base" Billing Acme.Shop 1
(cd "$base" && source_script && remove_generated Billing Acme.Shop) > /dev/null 2>&1 || fail "remove_generated Billing (parent with a file) failed"
assert_same_tree "remove_generated, parent not empty" "$before" "$base"
[[ -f "$base/src/Modules/Billing/notes.txt" ]] || fail "remove_generated: a file of the module folder the engine did not write was removed"

# A real module folder that no host registers (a module added by hand, not yet wired) is not touched by a run with a typo'd prefix.
base="$tmp/direct-unregistered"
build_base "$base"
mkdir -p "$base/src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement"
echo "// the module" > "$base/src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement/LeaveManagementModule.cs"
echo "# Leave management" > "$base/docs/modules/leave-management.md"
before="$(snapshot "$base")"
simulate_engine "$base" LeaveManagement Acme.Shopp
code="$(run_in "$base")"
assert_code "typo'd prefix next to an unregistered real module" 2 "$code"
assert_same_tree "typo'd prefix next to an unregistered real module" "$before" "$base"
grep -q "files are removed" "$tmp/out.log" || fail "typo'd prefix next to an unregistered real module: the message does not say the generated files were removed"

# It leaves a module alone that Program.cs already registers (the marker stays, as the run that refuses does not clean up).
base="$tmp/direct-audit"
build_base "$base"
printf 'Audit\nAcme.Shop\n' > "$base/.modulith-module"
before="$(snapshot "$base")"
for prefix in Acme.Shop Wrong.Name; do
  code=0
  (cd "$base" && source_script && remove_generated Audit "$prefix") > /dev/null 2>&1 || code=$?
  [[ "$code" != 0 ]] || fail "remove_generated Audit $prefix returned 0, expected non-zero (it must refuse)"
  assert_same_tree "remove_generated Audit $prefix" "$before" "$base"
done

# Without the marker nothing is removed at all, whatever the name and prefix (the files are not fresh from the engine).
base="$tmp/direct-no-marker"
build_base "$base"
simulate_engine "$base" Billing Wrong.Name 1
rm "$base/.modulith-module"
before="$(snapshot "$base")"
for prefix in Wrong.Name Acme.Shop; do
  code=0
  (cd "$base" && source_script && remove_generated Billing "$prefix") > /dev/null 2>&1 || code=$?
  [[ "$code" != 0 ]] || fail "remove_generated Billing $prefix without a marker returned 0, expected non-zero"
  assert_same_tree "remove_generated without a marker ($prefix)" "$before" "$base"
done

# A name that points outside the module folders: nothing is removed but the marker.
base="$tmp/direct-hostile"
build_base "$base"
before="$(snapshot "$base")"
for hostile in ".." "../Modules" "a/b"; do
  printf 'x\nAcme.Shop\n' > "$base/.modulith-module"
  code=0
  (cd "$base" && source_script && remove_generated "$hostile" Acme.Shop) > /dev/null 2>&1 || code=$?
  [[ "$code" != 0 ]] || fail "remove_generated '$hostile' returned 0, expected non-zero"
  assert_same_tree "remove_generated '$hostile'" "$before" "$base"
done

# A half-wired module (a run that stopped midway: the using is in Program.cs, the registration is not yet) is not removed either:
# a re-run with a typo'd prefix must leave the freshly generated files alone, and so must a using of any other prefix or case.
base="$tmp/half-wired"
build_base "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
{ echo "using Acme.Shop.Modules.LeaveManagement;"; cat "$base/src/Host/Acme.Shop.Api/Program.cs"; } > "$base/Program.cs.new"
mv "$base/Program.cs.new" "$base/src/Host/Acme.Shop.Api/Program.cs"
grep -q "AddLeaveManagementModule(" "$base/src/Host/Acme.Shop.Api/Program.cs" && fail "half-wired fixture: the registration is already there"
before="$(snapshot "$base")"
code="$(run_in "$base" --name LeaveManagement --prefix Wrong.Name)"
assert_code "half-wired, typo'd prefix" 2 "$code"
assert_same_tree "half-wired, typo'd prefix" "$before" "$base"
grep -q "files are removed" "$tmp/out.log" && fail "half-wired, typo'd prefix: the output says the files were removed although nothing was ($(tr '\n' ' ' < "$tmp/out.log"))"
grep -q "half wired" "$tmp/out.log" || fail "half-wired, typo'd prefix: the refusal message is missing"
for prefix in Acme.Shop Wrong.Name; do
  code=0
  (cd "$base" && source_script && remove_generated LeaveManagement "$prefix") > /dev/null 2>&1 || code=$?
  [[ "$code" != 0 ]] || fail "remove_generated half-wired $prefix returned 0, expected non-zero (it must refuse)"
  assert_same_tree "remove_generated half-wired $prefix" "$before" "$base"
done
content="$(< "$base/src/Host/Acme.Shop.Api/Program.cs")"
printf '%s\n' "${content/using Acme.Shop.Modules.LeaveManagement;/  using other.MODULES.leavemanagement;}" > "$base/src/Host/Acme.Shop.Api/Program.cs"
before="$(snapshot "$base")"
code=0
(cd "$base" && source_script && remove_generated LeaveManagement Wrong.Name) > /dev/null 2>&1 || code=$?
[[ "$code" != 0 ]] || fail "remove_generated with a using of another prefix and case returned 0, expected non-zero"
assert_same_tree "remove_generated, using of another prefix and case" "$before" "$base"

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
grep -q "files are removed" "$tmp/out.log" || fail "prefix Wrong.Name: the output does not say the generated files were removed"

base="$tmp/wrong-prefix-flags"
build_base "$base"
before="$(snapshot "$base")"
simulate_engine "$base" Billing Wrong.Name
code="$(run_in "$base" --name Billing --prefix Wrong.Name)"
assert_code "prefix Wrong.Name through the flags" 2 "$code"
assert_same_tree "prefix Wrong.Name through the flags" "$before" "$base"

# Without the marker the files are not fresh from the engine (a manual run, flags only): a wrong prefix or an invalid name exits 2
# and changes nothing, the module folder included.
base="$tmp/wrong-prefix-no-marker"
build_base "$base"
simulate_engine "$base" Billing Wrong.Name
rm "$base/.modulith-module"
before="$(snapshot "$base")"
code="$(run_in "$base" --name Billing --prefix Wrong.Name)"
assert_code "prefix Wrong.Name, flags only, no marker" 2 "$code"
assert_same_tree "prefix Wrong.Name, flags only, no marker" "$before" "$base"
grep -q "files are removed" "$tmp/out.log" && fail "prefix Wrong.Name, flags only, no marker: the output says files were removed"
code="$(run_in "$base" --name billing --prefix Acme.Shop)"
assert_code "invalid name, flags only, no marker" 2 "$code"
assert_same_tree "invalid name, flags only, no marker" "$before" "$base"

# --- a module that is already wired: exit 2 and nothing changes ---------------------------------------------------------------------

base="$tmp/wired"
build_base "$base"
printf 'Audit\nAcme.Shop\n' > "$base/.modulith-module"
before="$(snapshot "$base")"
code="$(run_in "$base" --name Audit --prefix Acme.Shop)"
assert_code "--name Audit" 2 "$code"
grep -q "already wired" "$tmp/out.log" || fail "--name Audit: the message does not say it is already wired"
# The message names the real values and is only the message: no exit code printed after it.
grep -q "Audit" "$tmp/out.log" && grep -q "Acme.Shop" "$tmp/out.log" || fail "--name Audit: the message does not name the module and the prefix ($(tr '\n' ' ' < "$tmp/out.log"))"
grep -Eq '[0-9]$' "$tmp/out.log" && fail "--name Audit: the message ends in a stray digit ($(tr '\n' ' ' < "$tmp/out.log"))"
grep -q '\.\.\.' "$tmp/out.log" && fail "--name Audit: the message still holds a placeholder"
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

# --- finish_module ends with status 0 once the commands succeeded, whatever the last git query says ---------------------------------

code=0
(
  source_script
  git() { if [[ "$1" == rev-parse ]]; then return 0; fi; return 1; }
  rm -f "$RECORD"
  if finish_module Billing Acme.Shop > /dev/null 2>&1; then exit 0; else exit 7; fi
) || code=$?
[[ "$code" == 0 ]] || fail "finish_module returned non-zero (exit $code) although all four commands succeeded"

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

# --- the edits at the anchors --------------------------------------------------------------------------------------------------------
# Expectations are written out here, independently of the script. A SPEC is NAME:CONTRACTS:KEBAB (CONTRACTS 0 or 1), in the order
# the modules were added. Lines are compared trimmed and without a trailing CR; a block must sit immediately before its anchor.

all_keys=(program-usings program-registrations program-endpoints api-references unit-references arch-references integration-references
  slnx-modules docker-copies assemblies-usings assemblies-modules assemblies-contracts assemblies-errors)

# anchor_of DIR KEY: sets a_file and a_text from the anchor list of the tree.
anchor_of() {
  local row_file row_anchor k
  a_file=""
  a_text=""
  while IFS='|' read -r k row_file row_anchor || [[ -n "$k" ]]; do
    if [[ "$k" == "$2" ]]; then
      a_file="$row_file"
      a_text="${row_anchor%$'\r'}"
      return 0
    fi
  done < "$1/build/scripts/module-anchors.txt"
  return 1
}

# wire_lines KEY NAME PREFIX CONTRACTS: sets block to the lines that must be inserted at the anchor of KEY.
wire_lines() {
  local key="$1" m="$2" p="$3" c="$4"
  local mod="$p.Modules.$m" con="$p.Modules.$m.Contracts"
  block=()
  case "$key" in
    program-usings) block=("using $mod;") ;;
    program-registrations) block=("builder.Services.Add${m}Module(builder.Configuration);") ;;
    program-endpoints) block=("api.Map${m}Endpoints();") ;;
    api-references)
      block=("<ProjectReference Include=\"..\\..\\Modules\\$m\\$mod\\$mod.csproj\" />")
      [[ "$c" == 1 ]] && block+=("<ProjectReference Include=\"..\\..\\Modules\\$m\\$con\\$con.csproj\" />")
      ;;
    unit-references | arch-references | integration-references)
      block=("<ProjectReference Include=\"..\\..\\src\\Modules\\$m\\$mod\\$mod.csproj\" />")
      [[ "$c" == 1 ]] && block+=("<ProjectReference Include=\"..\\..\\src\\Modules\\$m\\$con\\$con.csproj\" />")
      ;;
    slnx-modules)
      block=("<Folder Name=\"/src/Modules/$m/\">" "<Project Path=\"src/Modules/$m/$mod/$mod.csproj\" />")
      [[ "$c" == 1 ]] && block+=("<Project Path=\"src/Modules/$m/$con/$con.csproj\" />")
      block+=("</Folder>")
      ;;
    docker-copies)
      block=("COPY src/Modules/$m/$mod/$mod.csproj src/Modules/$m/$mod/packages.lock.json src/Modules/$m/$mod/")
      [[ "$c" == 1 ]] && block+=("COPY src/Modules/$m/$con/$con.csproj src/Modules/$m/$con/packages.lock.json src/Modules/$m/$con/")
      ;;
    assemblies-usings) block=("using $mod;" "using $mod.Resources;") ;;
    assemblies-modules) block=("typeof(${m}Module).Assembly,") ;;
    assemblies-contracts)
      [[ "$c" == 1 ]] && block=("Assembly.Load(\"$con\"),")
      ;;
    assemblies-errors) block=("typeof(${m}ErrorMessages),") ;;
    *) fail "wire_lines: unknown key $key" ;;
  esac
  return 0
}

# load_lines FILE: sets file_lines (trimmed, CR removed) and file_raw (CR removed only). Returns 1 for a missing file.
load_lines() {
  local line trimmed
  file_lines=()
  file_raw=()
  [[ -f "$1" ]] || return 1
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    file_raw+=("$line")
    trimmed="${line#"${line%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    file_lines+=("$trimmed")
  done < "$1"
  return 0
}

# count_exact TEXT: sets count to the number of file_lines equal to TEXT.
count_exact() {
  local entry
  count=0
  for entry in "${file_lines[@]}"; do
    [[ "$entry" == "$1" ]] && count=$((count + 1))
  done
  return 0
}

# first_index TEXT: sets index to the first position of TEXT in file_lines, or -1.
first_index() {
  local i
  index=-1
  for i in "${!file_lines[@]}"; do
    if [[ "${file_lines[i]}" == "$1" ]]; then
      index=$i
      return 0
    fi
  done
  return 0
}

# check_key DIR KEY PREFIX SPEC...: each inserted line is in the file exactly once (except the generic </Folder>) and the blocks of all
# SPECs, in order, sit immediately before the one anchor line.
check_key() {
  local dir="$1" key="$2" prefix="$3" spec name rest contracts line i n start
  shift 3
  local -a expected=()
  anchor_of "$dir" "$key" || { fail "$key: no anchor entry"; return; }
  load_lines "$dir/$a_file" || { fail "$key: cannot read $a_file"; return; }
  for spec in "$@"; do
    name="${spec%%:*}"
    rest="${spec#*:}"
    contracts="${rest%%:*}"
    wire_lines "$key" "$name" "$prefix" "$contracts"
    if ((${#block[@]} > 0)); then
      for line in "${block[@]}"; do
        expected+=("$line")
        [[ "$line" == "</Folder>" ]] && continue
        count_exact "$line"
        [[ "$count" == 1 ]] || fail "$key: '$line' occurs $count times in $a_file, expected 1"
      done
    fi
  done
  count_exact "$a_text"
  [[ "$count" == 1 ]] || fail "$key: the anchor '$a_text' occurs $count times in $a_file, expected 1"
  first_index "$a_text"
  n=${#expected[@]}
  start=$((index - n))
  if ((index < 0 || start < 0)); then
    fail "$key: the anchor is missing or too near the top of $a_file"
    return
  fi
  for ((i = 0; i < n; i++)); do
    [[ "${file_lines[start + i]}" == "${expected[i]}" ]] || fail "$key: line $((start + i + 1)) of $a_file is '${file_lines[start + i]}', expected '${expected[i]}'"
  done
}

# row_of FILE TEXT: sets row to the first CR-less line of FILE that contains TEXT (empty if none) and rows to how many contain it.
row_of() {
  local entry
  row=""
  rows=0
  load_lines "$1" || return 1
  for entry in "${file_raw[@]}"; do
    if [[ "$entry" == *"$2"* ]]; then
      [[ $rows == 0 ]] && row="$entry"
      rows=$((rows + 1))
    fi
  done
  return 0
}

# check_misc DIR PREFIX SPEC...: the scopes row, the docs index rows and ALLOWED_SCOPES. Honours skip_readme=1.
check_misc() {
  local dir="$1" spec name rest kebab added="" original_row expected_row scope_anchor='<!-- modulith:module-scopes -->'
  local -a index_rows=()
  shift 2
  for spec in "$@"; do
    name="${spec%%:*}"
    rest="${spec#*:}"
    kebab="${rest#*:}"
    added+=", \`$kebab\`"
    index_rows+=("| [\`modules/$kebab.md\`](modules/$kebab.md) | $name: new module (describe it) |")
  done

  row_of "$base_template/CONTRIBUTING.md" "$scope_anchor"
  original_row="$row"
  expected_row="${original_row%%"$scope_anchor"*}$added$scope_anchor${original_row#*"$scope_anchor"}"
  row_of "$dir/CONTRIBUTING.md" "$scope_anchor"
  [[ "$rows" == 1 && "$row" == "$expected_row" ]] || fail "CONTRIBUTING.md: row is '$row' ($rows rows), expected '$expected_row'"

  if [[ "${skip_readme:-0}" != 1 ]]; then
    local i n=${#index_rows[@]} start
    if load_lines "$dir/docs/README.md"; then
      first_index '| [`modules/_template.md`](modules/_template.md) | The required outline; copy it to `modules/{module}.md` when you add a module |'
      start=$((index - n))
      if ((index < 0 || start < 0)); then
        fail "docs/README.md: the _template row is missing"
      else
        for ((i = 0; i < n; i++)); do
          [[ "${file_lines[start + i]}" == "${index_rows[i]}" ]] || fail "docs/README.md: row $((start + i + 1)) is '${file_lines[start + i]}', expected '${index_rows[i]}'"
          count_exact "${index_rows[i]}"
          [[ "$count" == 1 ]] || fail "docs/README.md: '${index_rows[i]}' occurs $count times"
        done
      fi
    else
      fail "docs/README.md is missing"
    fi
  fi

  row_of "$base_template/build/scripts/check-commit-msg.sh" 'ALLOWED_SCOPES="'
  expected_row="${row%\"*}"
  for spec in "$@"; do
    rest="${spec#*:}"
    expected_row+="|${rest#*:}"
  done
  expected_row+='"'
  row_of "$dir/build/scripts/check-commit-msg.sh" 'ALLOWED_SCOPES="'
  [[ "$rows" == 1 && "$row" == "$expected_row" ]] || fail "check-commit-msg.sh: ALLOWED_SCOPES line is '$row' ($rows lines), expected '$expected_row'"
}

# check_all DIR PREFIX SPEC...: every key and the misc edits; except_key=KEY leaves one key out.
check_all() {
  local dir="$1" prefix="$2" key spec name rest
  shift 2
  for key in "${all_keys[@]}"; do
    [[ "$key" == "${except_key:-}" ]] && continue
    check_key "$dir" "$key" "$prefix" "$@"
  done
  check_misc "$dir" "$prefix" "$@"
  # A module without Contracts leaves no trace of a Contracts project in the shared files.
  for spec in "$@"; do
    name="${spec%%:*}"
    rest="${spec#*:}"
    if [[ "${rest%%:*}" != 1 ]]; then
      if grep -rlF --exclude-dir=Modules "Modules.$name.Contracts" "$dir" > "$tmp/grep.log" 2>&1; then
        fail "$name has no Contracts, but $(tr '\n' ' ' < "$tmp/grep.log")mention(s) it"
      fi
    fi
  done
}

# ends_with_newline FILE
ends_with_newline() {
  local content=""
  IFS= read -r -d '' content < "$1" || true
  [[ "$content" == *$'\n' ]]
}

# all_crlf FILE: every line ends with CR LF (a last line without any newline is exempt).
all_crlf() {
  local line
  while IFS= read -r line; do
    [[ "$line" == *$'\r' ]] || { fail "$1: a line does not end in CRLF: '$line'"; return; }
  done < "$1"
}

# to_crlf FILE [noeol]: rewrites FILE with CRLF line endings; with a second argument it also drops the final line ending.
to_crlf() {
  local line out=""
  while IFS= read -r line || [[ -n "$line" ]]; do
    out+="$line"$'\r\n'
  done < "$1"
  [[ -n "${2-}" ]] && out="${out%$'\r\n'}"
  printf '%s' "$out" > "$1"
}

# shared_paths DIR: sets shared to every file an edit touches (relative paths, each once).
shared_paths() {
  local k row_file row_anchor known=" "
  shared=()
  while IFS='|' read -r k row_file row_anchor || [[ -n "$k" ]]; do
    [[ -z "$k" || "$known" == *" $row_file "* ]] && continue
    known+="$row_file "
    shared+=("$row_file")
  done < "$1/build/scripts/module-anchors.txt"
  shared+=("build/scripts/module-anchors.txt" "docs/README.md" "build/scripts/check-commit-msg.sh")
}

# new_project DIR: a fresh fake project.
new_project() {
  mkdir -p "$(dirname "$1")"
  build_base "$1"
}

# 1. A full run without Contracts.
base="$tmp/wire-lm"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
rm -f "$RECORD"
code="$(run_in "$base")"
assert_code "wire LeaveManagement" 0 "$code"
check_all "$base" Acme.Shop LeaveManagement:0:leave-management
grep -q "add by hand" "$tmp/out.log" && fail "wire LeaveManagement: nothing should be left to do by hand"
# After the edits: the four dotnet commands, in this order, with exactly these arguments.
expected_calls=$'tool restore\nrestore\n'
expected_calls+=$'ef migrations add CreateLeaveManagementSchema --project src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement --startup-project src/Host/Acme.Shop.Api --context LeaveManagementDbContext --output-dir Infrastructure/Persistence/Migrations\n'
expected_calls+='build --no-restore -c Release'
[[ -f "$RECORD" && "$(< "$RECORD")" == "$expected_calls" ]] ||
  fail "wire LeaveManagement: the dotnet calls are not the expected four: $(tr '\n' '|' < "$RECORD" 2>/dev/null)"
# The output of the dotnet commands stays out of the script's own output (a large output blocks the script under the template engine,
# which reads it only after the script ends); the success path keeps no log file behind.
grep -q "stub output of" "$tmp/out.log" && fail "wire LeaveManagement: the dotnet output reached the script's output"
# Not a git work tree: the commit list is a note naming what to commit, the lock files included.
grep -q "commit these:" "$tmp/out.log" || fail "wire LeaveManagement: the output has no 'commit these:' line"
grep -q "not a git repository" "$tmp/out.log" || fail "wire LeaveManagement: no note that this is not a git repository"
grep -q "packages.lock.json" "$tmp/out.log" || fail "wire LeaveManagement: the note does not name the packages.lock.json files"
[[ ! -e "$base/.modulith-module" ]] || fail "wire LeaveManagement: the marker was not deleted"
# The slnx folder: the project two spaces deeper than the anchor, the closing tag at the anchor's indentation.
load_lines "$base/Acme.Shop.slnx"
first_index "<!-- modulith:modules -->"
[[ "${file_raw[index]}" == '  <!-- modulith:modules -->' ]] || fail "slnx: the anchor line changed"
[[ "${file_raw[index - 1]}" == '  </Folder>' ]] || fail "slnx: closing tag is '${file_raw[index - 1]}'"
[[ "${file_raw[index - 2]}" == '    <Project Path="src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement/Acme.Shop.Modules.LeaveManagement.csproj" />' ]] ||
  fail "slnx: project line is '${file_raw[index - 2]}'"
[[ "${file_raw[index - 3]}" == '  <Folder Name="/src/Modules/LeaveManagement/">' ]] || fail "slnx: folder line is '${file_raw[index - 3]}'"
# Program.cs: the registration keeps the anchor's (zero) indentation and sits right before it.
load_lines "$base/src/Host/Acme.Shop.Api/Program.cs"
first_index "// modulith:module-registrations"
[[ "${file_raw[index - 1]}" == 'builder.Services.AddLeaveManagementModule(builder.Configuration);' ]] || fail "Program.cs: registration line is '${file_raw[index - 1]}'"

# 4. A second run for the same module: exit 2 and nothing at all changes.
before="$(snapshot "$base")"
code="$(run_in "$base" --name LeaveManagement --prefix Acme.Shop)"
assert_code "second run" 2 "$code"
assert_same_tree "second run" "$before" "$base"

# 2. A full run with Contracts.
base="$tmp/wire-billing"
new_project "$base"
# A git work tree this time: the base is committed, the engine's files are new, so the commit list comes from git status and names
# every new file one by one (the lock files too), not only their folders.
echo "old lock" > "$base/src/Host/Acme.Shop.Api/packages.lock.json" # tracked, and rewritten by the restore below
(cd "$base" && git init -q . && git add -A && git -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false commit -q -m base) > /dev/null 2>&1 ||
  fail "wire Billing: could not prepare the git work tree"
simulate_engine "$base" Billing Acme.Shop 1
[[ -d "$base/src/Modules/Billing/Acme.Shop.Modules.Billing.Contracts" ]] || fail "the simulated engine wrote no Contracts folder"
rm -f "$RECORD"
code="$(LOCK_FILES="src/Modules/Billing/Acme.Shop.Modules.Billing/packages.lock.json src/Modules/Billing/Acme.Shop.Modules.Billing.Contracts/packages.lock.json src/Host/Acme.Shop.Api/packages.lock.json" run_in "$base")"
assert_code "wire Billing with Contracts" 0 "$code"
grep -q "commit these:" "$tmp/out.log" || fail "wire Billing: the output has no 'commit these:' line"
grep -q "not a git repository" "$tmp/out.log" && fail "wire Billing: a git work tree was reported as not a git repository"
for path in "src/Modules/Billing/Acme.Shop.Modules.Billing/packages.lock.json" "src/Modules/Billing/Acme.Shop.Modules.Billing.Contracts/packages.lock.json" \
  "docs/modules/billing.md" "src/Host/Acme.Shop.Api/Program.cs"; do
  grep -qF -- "$path" "$tmp/out.log" || fail "wire Billing: the commit list does not name $path ($(tr '\n' '|' < "$tmp/out.log"))"
done
# Each lock file is listed once: the new ones by the second query, a tracked and modified one by the first only.
for path in "src/Modules/Billing/Acme.Shop.Modules.Billing/packages.lock.json" "src/Host/Acme.Shop.Api/packages.lock.json"; do
  [[ "$(grep -cF -- "$path" "$tmp/out.log")" == 1 ]] || fail "wire Billing: $path is listed $(grep -cF -- "$path" "$tmp/out.log") times, expected once ($(tr '\n' '|' < "$tmp/out.log"))"
done
check_all "$base" Acme.Shop Billing:1:billing
load_lines "$base/Acme.Shop.slnx"
first_index "<!-- modulith:modules -->"
[[ "${file_raw[index - 1]}" == '  </Folder>' && "${file_raw[index - 2]}" == '    <Project Path="src/Modules/Billing/Acme.Shop.Modules.Billing.Contracts/Acme.Shop.Modules.Billing.Contracts.csproj" />' ]] ||
  fail "slnx: the Contracts project line is missing or misplaced"

# 3. Two modules in sequence: both blocks, in order, anchors still last.
base="$tmp/wire-two"
new_project "$base"
simulate_engine "$base" Billing Acme.Shop 1
code="$(run_in "$base")"
assert_code "two modules: Billing" 0 "$code"
simulate_engine "$base" LeaveManagement Acme.Shop
code="$(run_in "$base")"
assert_code "two modules: LeaveManagement" 0 "$code"
check_all "$base" Acme.Shop Billing:1:billing LeaveManagement:0:leave-management

# 5. One anchor is gone: that edit is skipped and reported, everything else is done, exit 1.
base="$tmp/wire-no-anchor"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
load_lines "$base/tests/Acme.Shop.ArchitectureTests/Assemblies.cs"
{
  for line in "${file_raw[@]}"; do
    [[ "${line#"${line%%[![:space:]]*}"}" == "// modulith:modules" ]] || printf '%s\n' "$line"
  done
} > "$base/tests/Acme.Shop.ArchitectureTests/Assemblies.cs"
rm -f "$RECORD"
code="$(run_in "$base")"
assert_code "one anchor missing" 1 "$code"
# A skipped edit: the dotnet steps are not run, they are printed to run by hand.
[[ ! -s "$RECORD" ]] || fail "one anchor missing: dotnet was run although an edit was skipped ($(tr '\n' '|' < "$RECORD"))"
grep -qxF 'add-module: then run: dotnet tool restore' "$tmp/out.log" || fail "one anchor missing: no 'then run: dotnet tool restore' line"
grep -qxF 'add-module: then run: dotnet ef migrations add CreateLeaveManagementSchema --project src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement --startup-project src/Host/Acme.Shop.Api --context LeaveManagementDbContext --output-dir Infrastructure/Persistence/Migrations' "$tmp/out.log" ||
  fail "one anchor missing: no 'then run: dotnet ef migrations add ...' line"
grep -qxF 'add-module: then run: dotnet restore' "$tmp/out.log" || fail "one anchor missing: no 'then run: dotnet restore' line"
grep -qxF 'add-module: then run: dotnet build --no-restore -c Release' "$tmp/out.log" || fail "one anchor missing: no 'then run: dotnet build' line"
grep -qxF 'add-module: add by hand: tests/Acme.Shop.ArchitectureTests/Assemblies.cs: typeof(LeaveManagementModule).Assembly,' "$tmp/out.log" ||
  fail "one anchor missing: the output does not list the line to add by hand ($(tr '\n' ' ' < "$tmp/out.log"))"
[[ "$(grep -c 'add by hand' "$tmp/out.log")" == 1 ]] || fail "one anchor missing: more than one entry listed"
except_key=assemblies-modules check_all "$base" Acme.Shop LeaveManagement:0:leave-management
load_lines "$base/tests/Acme.Shop.ArchitectureTests/Assemblies.cs"
count_exact "typeof(LeaveManagementModule).Assembly,"
[[ "$count" == 0 ]] || fail "one anchor missing: the skipped line was inserted anyway"
[[ ! -e "$base/.modulith-module" ]] || fail "one anchor missing: the marker was not deleted"

# 5b. The migration step fails: exit 1, the message names the step, the build never runs, the marker is gone.
base="$tmp/wire-ef-fails"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
rm -f "$RECORD"
code="$(FAIL_ON="ef " run_in "$base")"
assert_code "failing ef step" 1 "$code"
grep -q "ef migrations add" "$tmp/out.log" || fail "failing ef step: the message does not name the step ($(tr '\n' ' ' < "$tmp/out.log"))"
# The failed command's own output is shown (its last lines), from the log file the script keeps.
grep -q "stub output of: ef migrations add" "$tmp/out.log" || fail "failing ef step: the output of the failed command is not shown"
grep -qxF 'add-module: then run: dotnet build --no-restore -c Release' "$tmp/out.log" || fail "failing ef step: the later step is not listed to run by hand"
[[ "$(wc -l < "$RECORD" | tr -d ' ')" == 3 ]] || fail "failing ef step: expected exactly three dotnet calls (the build must not run), got $(tr '\n' '|' < "$RECORD")"
[[ "$(sed -n 1p "$RECORD")" == "tool restore" && "$(sed -n 2p "$RECORD")" == "restore" && "$(sed -n 3p "$RECORD")" == "ef migrations add CreateLeaveManagementSchema "* ]] ||
  fail "failing ef step: the recorded calls are $(tr '\n' '|' < "$RECORD")"
[[ ! -e "$base/.modulith-module" ]] || fail "failing ef step: the marker was not deleted"

# 6. A missing target file is skipped like a missing anchor.
base="$tmp/wire-no-file"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
rm "$base/docs/README.md"
code="$(run_in "$base")"
assert_code "docs/README.md missing" 1 "$code"
grep -qxF 'add-module: add by hand: docs/README.md: | [`modules/leave-management.md`](modules/leave-management.md) | LeaveManagement: new module (describe it) |' "$tmp/out.log" ||
  fail "docs/README.md missing: the output does not list the row to add ($(tr '\n' ' ' < "$tmp/out.log"))"
skip_readme=1 check_all "$base" Acme.Shop LeaveManagement:0:leave-management
[[ ! -e "$base/.modulith-module" ]] || fail "docs/README.md missing: the marker was not deleted"

# 7. CRLF: every shared file in CRLF (the Dockerfile without a final newline): inserted lines are CRLF, the end state is kept.
base="$tmp/wire-crlf"
new_project "$base"
shared_paths "$base"
for path in "${shared[@]}"; do
  if [[ "$path" == "build/docker/Dockerfile" ]]; then to_crlf "$base/$path" noeol; else to_crlf "$base/$path"; fi
done
simulate_engine "$base" Billing Acme.Shop 1
code="$(run_in "$base")"
assert_code "CRLF tree" 0 "$code"
check_all "$base" Acme.Shop Billing:1:billing
for path in "${shared[@]}"; do
  if [[ "$path" == "build/docker/Dockerfile" ]]; then
    ends_with_newline "$base/$path" && fail "CRLF tree: $path gained a final newline"
  else
    ends_with_newline "$base/$path" || fail "CRLF tree: $path lost its final newline"
  fi
  all_crlf "$base/$path"
done
# A pure-LF tree stays pure LF.
base="$tmp/wire-lf"
new_project "$base"
simulate_engine "$base" Billing Acme.Shop 1
code="$(run_in "$base")"
assert_code "LF tree" 0 "$code"
shared_paths "$base"
for path in "${shared[@]}"; do
  grep -q $'\r' "$base/$path" && fail "LF tree: $path gained a CR"
done

# 8. A solution root with spaces in its path.
base="$tmp/lee jia le/proj dir"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop 1
code="$(run_in "$base")"
assert_code "path with spaces" 0 "$code"
check_all "$base" Acme.Shop LeaveManagement:1:leave-management

# 9. A failure after the other edits but before the Program.cs registration leaves the module unwired; a re-run completes it.
base="$tmp/wire-partial"
new_project "$base"
simulate_engine "$base" LeaveManagement Acme.Shop
code=0
(
  cd "$base" && source "$script" &&
    edit_commit_scopes() {
      echo "forced failure" >&2
      exit 9
    } && main
) > "$tmp/out.log" 2>&1 || code=$?
assert_code "forced failure before the registration" 9 "$code"
load_lines "$base/src/Host/Acme.Shop.Api/Program.cs"
count_exact "builder.Services.AddLeaveManagementModule(builder.Configuration);"
[[ "$count" == 0 ]] || fail "partial run: Program.cs already registers the module"
check_key "$base" api-references Acme.Shop LeaveManagement:0:leave-management
check_key "$base" assemblies-modules Acme.Shop LeaveManagement:0:leave-management
code="$(run_in "$base" --name LeaveManagement --prefix Acme.Shop)"
assert_code "re-run after a partial failure" 0 "$code"
check_all "$base" Acme.Shop LeaveManagement:0:leave-management

# insert_before_anchor on its own: CRLF with the anchor on a last line without a newline, indentation, idempotency, a missing anchor.
mini="$tmp/mini"
mkdir -p "$mini/build/scripts"
printf 'k|f.txt|# anchor\nm|g.txt|# nowhere\nn|missing.txt|# anchor\n' > "$mini/build/scripts/module-anchors.txt"
printf 'a\r\n# anchor' > "$mini/f.txt"
printf 'x\n' > "$mini/g.txt"
(cd "$mini" && source_script && insert_before_anchor k "X" "  Y") > /dev/null 2>&1 || fail "insert_before_anchor failed on a CRLF file"
[[ "$(< "$mini/f.txt")" == $'a\r\nX\r\n  Y\r\n# anchor' ]] || fail "insert_before_anchor (CRLF, no final newline): got $(od -c "$mini/f.txt" | head -3 | tr '\n' ' ')"
ends_with_newline "$mini/f.txt" && fail "insert_before_anchor: a final newline appeared"
before="$(snapshot "$mini")"
(cd "$mini" && source_script && insert_before_anchor k "X" "  Y") > /dev/null 2>&1 || fail "insert_before_anchor failed on the second call"
assert_same_tree "insert_before_anchor idempotency" "$before" "$mini"
printf 'a\n    # anchor\nb\n' > "$mini/f.txt"
(cd "$mini" && source_script && insert_before_anchor k "X" "  Y") > /dev/null 2>&1 || fail "insert_before_anchor failed on an indented anchor"
[[ "$(< "$mini/f.txt")" == $'a\n    X\n      Y\n    # anchor\nb' ]] || fail "insert_before_anchor (indented): got $(od -c "$mini/f.txt" | head -3 | tr '\n' ' ')"
ends_with_newline "$mini/f.txt" || fail "insert_before_anchor: the final newline was lost"
skipped_list="$(cd "$mini" && source_script && insert_before_anchor m "P" "Q"; insert_before_anchor n "R"; printf '%s;' "${skipped[@]}")"
[[ "$skipped_list" == "g.txt: P;g.txt: Q;missing.txt: R;" ]] || fail "insert_before_anchor: skipped list is '$skipped_list'"

if ((failures > 0)); then
  echo "$failures failure(s)" >&2
  exit 1
fi
echo "add-module tests passed"
