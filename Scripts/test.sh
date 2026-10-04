#!/bin/bash
# Runs every automated check: self-test (links, config, fuzz, live routing, 6-protocol E2E)
# and the root-script tests. No root needed.
# Usage: Scripts/test.sh [--fuzz N]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if ! xcodebuild -version >/dev/null 2>&1 && [ -d /Library/Developer/CommandLineTools ]; then
    export DEVELOPER_DIR=/Library/Developer/CommandLineTools
fi
SB="$ROOT/vendor/sing-box"
# The self-test expects the shipped rule sets next to sing-box (vendor/rule-sets).
[ -x "$SB" ] || "$ROOT/Scripts/build.sh" >/dev/null
cd "$ROOT/App"
swift build
./.build/debug/NovaSelfTest "$SB" "$@"
echo
./Tests/scripts-test.sh "$SB"
