#!/usr/bin/env bash
# Turns the maintenance dispatch input and the detection answer into the final decision (maintenance screen design, section 5).
#   auto (or empty): the detection answer.   on: true.   off: false, with a warning annotation when detection said true.
#
# Usage: bash build/scripts/resolve-maintenance.sh <mode> <detected>
#   mode: auto, "" (same as auto), on or off.   detected: the output of detect-contract-migration.sh ("true" or "false").
# Output: "true" or "false" on stdout, nothing else. Exit 1 on any other mode or detected value. Everything else goes to stderr,
# where the GitHub runner reads the ::warning and ::notice annotations.
set -euo pipefail

fail() { echo "resolve-maintenance: $*" >&2; exit 1; }

(( $# == 2 )) || fail "usage: resolve-maintenance.sh <mode> <detected>"
mode="$1"
detected="$2"

case "$detected" in
  true | false) ;;
  *) fail "detected must be 'true' or 'false', got '$detected'" ;;
esac

case "$mode" in
  "" | auto)
    echo "$detected"
    ;;
  on)
    if [[ "$detected" == "false" ]]; then
      echo "::notice title=Maintenance mode::Maintenance mode is forced on although no contract migration was detected." >&2
    fi
    echo "true"
    ;;
  off)
    if [[ "$detected" == "true" ]]; then
      echo "::warning title=Maintenance mode::Maintenance mode is forced off but this release adds a contract migration, so it deploys with the old code running on a contract schema." >&2
    fi
    echo "false"
    ;;
  *)
    fail "unknown mode '$mode': expected 'auto', 'on' or 'off'"
    ;;
esac
