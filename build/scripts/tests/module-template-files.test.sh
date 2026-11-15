#!/usr/bin/env bash
# Test for the modulith-module item template (templates/modulith-module): installs it into a private hive, generates two modules
# and asserts the files, names and substituted content. It does not run the wiring script: the generated trees are only checked.
#
# The template has a post-action (it runs build/scripts/add-module.sh), so every `dotnet new` call passes --allow-scripts no: the
# engine then writes the files, does not run the script and exits 104. This test asserts exactly that (the marker file stays and a
# stand-in script in the output folder is not run). It must never call `dotnet new modulith-module` without an explicit
# --allow-scripts value: without one a non-interactive run loops forever on the engine prompt.
#
# Usage: bash build/scripts/tests/module-template-files.test.sh   (from any directory)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
template="$repo_root/templates/modulith-module"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

tmp="$(mktemp -d)"
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT
hive="$tmp/hive"

if [[ ! -f "$template/.template.config/template.json" ]]; then
  echo "FAIL: $template/.template.config/template.json does not exist" >&2
  exit 1
fi

dotnet new install "$template" --force --debug:custom-hive "$hive" > "$tmp/install.log" 2>&1 \
  || { cat "$tmp/install.log" >&2; echo "FAIL: dotnet new install failed" >&2; exit 1; }

# generate <output folder> <dotnet new arguments...>: always with --allow-scripts no (never without a value, see the header), under
# a timeout in case the engine ever prompts. Sets generate_code to the exit code of `dotnet new`, which must be 104.
generate_code=0
generate() {
  local out="$1"
  shift
  generate_code=0
  timeout 600 dotnet new modulith-module "$@" -o "$out" --allow-scripts no --debug:custom-hive "$hive" > "$out.log" 2>&1 || generate_code=$?
}

# assert_files <output folder> <expected files, one per line>: the generated tree is exactly these files.
assert_files() {
  local out="$1" expected="$2" actual
  actual="$(cd "$out" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)"
  expected="$(printf '%s\n' "$expected" | LC_ALL=C sort)"
  if [[ "$actual" != "$expected" ]]; then
    fail "$out: generated files differ from the expected list"
    diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
  fi
}

# module_files <module> <prefix>: the files under the first project of a module, then the marker and the document.
module_files() {
  local m="$1" p="$2" root="src/Modules/$1/$2.Modules.$1"
  cat <<EOF
.modulith-module
docs/modules/$m.md
$root/$p.Modules.$m.csproj
$root/${m}Module.cs
$root/Application/Abstractions/IUnitOfWork.cs
$root/Application/${m}Permissions.cs
$root/Application/${m}PermissionSource.cs
$root/Infrastructure/Persistence/${m}DbContext.cs
$root/Resources/${m}ErrorMessages.cs
$root/Resources/${m}ErrorMessages.resx
$root/Resources/${m}ErrorMessages.ms.resx
$root/Resources/${m}ErrorMessages.zh-Hans.resx
$root/Domain/.gitkeep
EOF
}

# contracts_files <module> <prefix>
contracts_files() {
  local m="$1" p="$2" root="src/Modules/$1/$2.Modules.$1.Contracts"
  cat <<EOF
$root/$p.Modules.$m.Contracts.csproj
$root/Public/.gitkeep
EOF
}

# assert_clean <output folder>: no placeholder is left in a file or folder name or in any content.
assert_clean() {
  local out="$1" names contents
  names="$(cd "$out" && find . | grep -E 'ModuleName|ProjectPrefix' || true)"
  [[ -z "$names" ]] || fail "$out: a name still carries a placeholder: $names"
  contents="$(grep -rlE 'ModuleName|ProjectPrefix|module-lower' "$out" || true)"
  [[ -z "$contents" ]] || fail "$out: a file still carries a placeholder: $contents"
}

# --- LeaveManagement with Contracts ------------------------------------------------------------------------------------------
lm="$tmp/lm"
# A stand-in for the wiring script already sits in the output folder: with --allow-scripts no the engine must not run it.
mkdir -p "$lm/build/scripts"
printf '#!/usr/bin/env bash\necho ran > stub-ran.txt\n' > "$lm/build/scripts/add-module.sh"
generate "$lm" -n LeaveManagement --prefix Acme.Shop --contracts true
[[ "$generate_code" == 104 ]] || { cat "$lm.log" >&2; fail "LeaveManagement: dotnet new exited $generate_code under --allow-scripts no, expected 104"; }

[[ -f "$lm/.modulith-module" ]] || { cat "$lm.log" >&2; echo "FAIL: nothing was generated for LeaveManagement" >&2; exit 1; }
[[ ! -e "$lm/stub-ran.txt" ]] || fail "LeaveManagement: the post-action ran the script although --allow-scripts no was given"

assert_files "$lm" "$(module_files LeaveManagement Acme.Shop; contracts_files LeaveManagement Acme.Shop; echo build/scripts/add-module.sh)"
assert_clean "$lm"

lm_project="$lm/src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement"
lm_context="$lm_project/Infrastructure/Persistence/LeaveManagementDbContext.cs"

if [[ -f "$lm_context" ]]; then
  grep -qF 'namespace Acme.Shop.Modules.LeaveManagement.Infrastructure.Persistence;' "$lm_context" \
    || fail "the DbContext namespace does not follow its folder"
  grep -qF 'internal const string Schema = "leavemanagement";' "$lm_context" \
    || fail "the DbContext schema constant is not the lower-case module name"
fi

if [[ -f "$lm_project/LeaveManagementModule.cs" ]]; then
  grep -qF 'namespace Acme.Shop.Modules.LeaveManagement;' "$lm_project/LeaveManagementModule.cs" \
    || fail "the module class namespace is not Acme.Shop.Modules.LeaveManagement"
fi

if [[ -f "$lm_project/Application/Abstractions/IUnitOfWork.cs" ]]; then
  grep -qF 'namespace Acme.Shop.Modules.LeaveManagement.Application.Abstractions;' "$lm_project/Application/Abstractions/IUnitOfWork.cs" \
    || fail "the IUnitOfWork namespace does not follow its folder"
fi

lm_doc="$lm/docs/modules/LeaveManagement.md"
if [[ -f "$lm_doc" ]]; then
  [[ "$(head -n 1 "$lm_doc")" == "# LeaveManagement module" ]] || fail "the module document does not start with '# LeaveManagement module'"
fi

marker="$lm/.modulith-module"
if [[ -f "$marker" ]]; then
  [[ "$(tr -d '\r' < "$marker")" == $'LeaveManagement\nAcme.Shop' ]] \
    || fail "the marker file does not hold exactly the lines LeaveManagement and Acme.Shop"
fi

lm_contracts="$lm/src/Modules/LeaveManagement/Acme.Shop.Modules.LeaveManagement.Contracts/Acme.Shop.Modules.LeaveManagement.Contracts.csproj"
if [[ -f "$lm_contracts" ]]; then
  [[ "$(grep -c 'ProjectReference' "$lm_contracts")" == "1" ]] || fail "the Contracts project must have exactly one ProjectReference"
  grep -qF 'Acme.Shop.SharedKernel.csproj' "$lm_contracts" || fail "the Contracts project must reference SharedKernel"
fi

# --- Billing without Contracts -------------------------------------------------------------------------------------------------
bl="$tmp/bl"
generate "$bl" -n Billing --prefix Acme.Shop
[[ "$generate_code" == 104 ]] || { cat "$bl.log" >&2; fail "Billing: dotnet new exited $generate_code under --allow-scripts no, expected 104"; }

[[ -f "$bl/.modulith-module" ]] || { cat "$bl.log" >&2; echo "FAIL: nothing was generated for Billing" >&2; exit 1; }

assert_files "$bl" "$(module_files Billing Acme.Shop)"
assert_clean "$bl"
[[ ! -e "$bl/src/Modules/Billing/Acme.Shop.Modules.Billing.Contracts" ]] || fail "Billing has a Contracts project without --contracts true"

bl_context="$bl/src/Modules/Billing/Acme.Shop.Modules.Billing/Infrastructure/Persistence/BillingDbContext.cs"
if [[ -f "$bl_context" ]]; then
  grep -qF 'internal const string Schema = "billing";' "$bl_context" || fail "the Billing schema constant is not 'billing'"
fi

if ((failures > 0)); then
  echo "module-template-files: $failures failure(s)" >&2
  exit 1
fi

echo "module-template-files: ok"
