#!/usr/bin/env bash
# Validates a commit message header against CONTRIBUTING.md Section 2 (Conventional Commits).
#
# Usage:
#   check-commit-msg.sh [--allow-autosquash] <message-file>     (git commit-msg hook)
#   check-commit-msg.sh [--allow-autosquash] --message "<text>" (CI, PR titles)
#
# Only the first non-comment, non-blank line is checked.
set -euo pipefail

# Make [A-Z] mean ASCII upper case regardless of the caller's locale.
export LC_ALL=C

ALLOWED_TYPES="feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert"
ALLOWED_SCOPES="shared-kernel|application|infrastructure|web|host|persistence|outbox|idempotency|observability|security|i18n|architecture|sample|auth|notifications|audit|ci|deps|template|docker|adr|release"
MAX_HEADER_LENGTH=72

allow_autosquash=false
message=""
message_file=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --allow-autosquash)
      allow_autosquash=true
      shift
      ;;
    --message)
      if [[ $# -lt 2 ]]; then
        echo "check-commit-msg: --message needs a value" >&2
        exit 2
      fi
      message="$2"
      shift 2
      ;;
    *)
      message_file="$1"
      shift
      ;;
  esac
done

if [[ -z "$message" && -n "$message_file" ]]; then
  message="$(cat "$message_file")"
fi

if [[ -z "$message" ]]; then
  echo "check-commit-msg: no commit message given (pass a file path or --message \"<text>\")" >&2
  exit 2
fi

header=""
while IFS= read -r line || [[ -n "$line" ]]; do
  line="${line%$'\r'}"
  if [[ "$line" == \#* || -z "${line//[[:space:]]/}" ]]; then
    continue
  fi
  header="$line"
  break
done <<< "$message"

fail() {
  echo "Invalid commit message header: $header" >&2
  echo "Reason: $1" >&2
  echo "See CONTRIBUTING.md Section 2 for the format, allowed types and scopes." >&2
  exit 1
}

if [[ -z "$header" ]]; then
  fail "the message is empty"
fi

if [[ "$allow_autosquash" == true && "$header" =~ ^(Merge |fixup\! |squash\! ) ]]; then
  exit 0
fi

if (( ${#header} > MAX_HEADER_LENGTH )); then
  fail "the header is ${#header} characters; the maximum is ${MAX_HEADER_LENGTH}"
fi

pattern="^(${ALLOWED_TYPES})(\((${ALLOWED_SCOPES})\))?!?: [^A-Z[:space:]].*[^.[:space:]]\$"
if [[ ! "$header" =~ $pattern ]]; then
  fail "expected '<type>(<scope>)!: <subject>' with a lower-case imperative subject and no trailing period; types: ${ALLOWED_TYPES//|/, }; scopes: ${ALLOWED_SCOPES//|/, }"
fi
