#!/bin/bash
# Packs build/VPN Nova.app into releases/VPN-Nova-<version>.dmg (drag-to-Applications layout).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/App/VERSION")"
APP="$ROOT/build/VPN Nova.app"
[ -d "$APP" ] || "$ROOT/Scripts/build.sh"
OUT="$ROOT/releases/VPN-Nova-$VERSION.dmg"
STAGE="$(mktemp -d)"
ditto "$APP" "$STAGE/VPN Nova.app"
ln -s /Applications "$STAGE/Applications"
mkdir -p "$ROOT/releases"
rm -f "$OUT"
hdiutil create -quiet -volname "VPN Nova $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 "$OUT"
rm -rf "$STAGE"
shasum -a 256 "$OUT" | sed "s|$ROOT/releases/||" > "$OUT.sha256"
echo "✓ $OUT"
cat "$OUT.sha256"
