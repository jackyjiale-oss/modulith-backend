#!/usr/bin/env bash
# Release helper for the template package (Modulith.Backend.Templates): checks a release tag against Version.props and packs
# the template with `dotnet pack build/pack/Modulith.Templates.csproj`. Repo-only: the generated project does not get this script.
#
# Usage:
#   bash build/scripts/release-package.sh check-tag TAG [VERSION_PROPS]
#       TAG must look like v1.2.3 or v1.2.3-rc.1 and must equal "v" + the <Version> element of VERSION_PROPS (default:
#       Version.props in the current directory). Prints the version (the tag without its v) and exits 0; exits 1 with a message on
#       stderr when the tag is malformed or does not match; exits 2 on a missing argument or an unknown command.
#   bash build/scripts/release-package.sh pack VERSION OUTDIR
#       VERSION is major.minor.patch with an optional pre-release suffix (no v). Packs into OUTDIR and prints the absolute path of
#       the .nupkg as the last line of stdout; exits 1 when the version is malformed or no .nupkg results.
#       The words of $RELEASE_PACKAGE_PACK_ARGS are added to the dotnet pack command (a hook for tests).
#
# Both commands validate their arguments before they read or write anything else.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
pack_project="build/pack/Modulith.Templates.csproj"
version_pattern='[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?'

usage() {
  echo "usage: release-package.sh check-tag TAG [VERSION_PROPS] | pack VERSION OUTDIR" >&2
  exit 2
}

die() {
  echo "release-package: $*" >&2
  exit 1
}

# is_version <text>: major.minor.patch with an optional pre-release suffix, nothing before or after.
is_version() {
  [[ "$1" =~ ^${version_pattern}$ ]]
}

# props_version <file>: the text of the first <Version> element, without surrounding spaces; tolerant of CRLF line endings.
props_version() {
  tr -d '\r' < "$1" | sed -n 's|.*<Version>[[:space:]]*\([^<]*[^<[:space:]]\)[[:space:]]*</Version>.*|\1|p' | head -n 1
}

check_tag() {
  (($# >= 1 && $# <= 2)) || usage
  local tag="$1" props="${2:-Version.props}" version declared
  [[ "$tag" == v* ]] && is_version "${tag#v}" || die "the tag '$tag' is not of the form v1.2.3 or v1.2.3-rc.1"
  version="${tag#v}"
  [[ -f "$props" ]] || die "$props does not exist"
  declared="$(props_version "$props")"
  [[ -n "$declared" ]] || die "$props holds no <Version> element"
  [[ "$declared" == "$version" ]] || die "the tag $tag does not match the version $declared in $props"
  printf '%s\n' "$version"
}

pack() {
  (($# == 2)) || usage
  local version="$1" outdir="$2" out nupkg
  is_version "$version" || die "the version '$version' is not of the form 1.2.3 or 1.2.3-rc.1"
  [[ -n "$outdir" ]] || die "the output folder is empty"
  mkdir -p "$outdir"
  out="$(cd "$outdir" && pwd)"
  # RELEASE_PACKAGE_PACK_ARGS is split on purpose: it holds extra dotnet pack words.
  # shellcheck disable=SC2086
  (cd "$repo_root" && dotnet pack "$pack_project" -c Release "-p:Version=$version" -o "$out" ${RELEASE_PACKAGE_PACK_ARGS:-}) >&2 \
    || die "dotnet pack failed"
  nupkg="$out/Modulith.Backend.Templates.$version.nupkg"
  [[ -f "$nupkg" ]] || die "dotnet pack produced no $nupkg"
  printf '%s\n' "$nupkg"
}

(($# >= 1)) || usage
command="$1"
shift
case "$command" in
  check-tag) check_tag "$@" ;;
  pack) pack "$@" ;;
  *) usage ;;
esac
