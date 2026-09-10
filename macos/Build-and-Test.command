#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$ROOT/../dist/macos"
LOG="$ROOT/../dist/macos/build-test.log"
echo "Codex Usage Bar 0.7.6 · Apple Silicon build and test"
build_status=0
if bash "$ROOT/build.sh" 2>&1 | tee "$LOG"; then
  open "$ROOT/../dist/macos"
  echo "Success. Move Codex Usage Bar.app into Applications, then open it."
else
  build_status=1
  echo "Build/test failed. Please send dist/macos/build-test.log back to the developer."
fi
read -r -p "Press Return to close… " _answer
exit "$build_status"
