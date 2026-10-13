#!/bin/bash
set -euo pipefail
task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_tmp="$(mktemp -d "${TMPDIR:-/tmp}/clipbridge-tests.XXXXXX")"
trap 'rm -rf "$task_tmp"' EXIT
# Typecheck the production AppKit implementation as well as running the real cleanup function.
swiftc -typecheck "$task_root/main.swift"
awk '/^func cleanupOldScreenshots/ { copying=1 } /^class ClipboardWatcher/ { exit } copying { print }' \
    "$task_root/main.swift" > "$task_tmp/CleanupRegression.swift"
cat "$task_root/tests/CleanupRegression.swift" >> "$task_tmp/CleanupRegression.swift"
swiftc -o "$task_tmp/cleanup-regression" "$task_tmp/CleanupRegression.swift"
"$task_tmp/cleanup-regression"
