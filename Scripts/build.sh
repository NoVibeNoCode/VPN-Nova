#!/bin/bash
# Builds "build/VPN Nova.app": release binary + sing-box + root scripts + fonts + icon.
# Usage: Scripts/build.sh [--install]   (--install copies the app to /Applications)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/App/VERSION")"
# Root part (core + install.sh/novactl/plist). Bump only when those change: users get "Обновить службу".
SERVICE_VERSION="$(tr -d '[:space:]' < "$ROOT/App/SERVICE_VERSION")"
SB_VERSION="1.14.2-nova.1"   # official v1.14.2 source + App/Patches (Scripts/build-core.sh)

# Xcode's license may be unaccepted; Command Line Tools are enough.
if ! xcodebuild -version >/dev/null 2>&1 && [ -d /Library/Developer/CommandLineTools ]; then
    export DEVELOPER_DIR=/Library/Developer/CommandLineTools
fi

# sing-box core: official source tag + Nova's patches.
SB="$ROOT/vendor/sing-box"
if [ ! -x "$SB" ] || ! "$SB" version 2>/dev/null | grep -q "version $SB_VERSION"; then
    "$ROOT/Scripts/build-core.sh"
fi

# Rule sets shipped with the app (connect never waits for GitHub).
SETS="$ROOT/vendor/rule-sets"
mkdir -p "$SETS"
for t in geosite-youtube geosite-google geosite-telegram geosite-openai geosite-anthropic geosite-instagram \
         geosite-facebook geosite-twitter geosite-discord geosite-netflix geosite-spotify geosite-github geosite-cn \
         geoip-cn geoip-ru; do
    [ -s "$SETS/$t.srs" ] && continue
    repo=sing-geosite; [[ $t == geoip-* ]] && repo=sing-geoip
    curl -fsSL -m 30 -o "$SETS/$t.srs" "https://raw.githubusercontent.com/SagerNet/$repo/rule-set/$t.srs" || echo "⚠ не скачался $t"
done

echo "→ swift build"
cd "$ROOT/App"
swift build -c release --product Nova
swift build -c release --product NovaGuard
BIN="$(swift build -c release --show-bin-path)/Nova"
GUARD_BIN="$(swift build -c release --show-bin-path)/NovaGuard"

ICNS="$ROOT/build/AppIcon.icns"
if [ ! -f "$ICNS" ] || [ "$ROOT/Scripts/make-icon.swift" -nt "$ICNS" ]; then
    echo "→ icon"
    mkdir -p "$ROOT/build"
    rm -rf "$ROOT/build/AppIcon.iconset"
    swift "$ROOT/Scripts/make-icon.swift" "$ROOT/build/AppIcon.iconset"
    iconutil -c icns "$ROOT/build/AppIcon.iconset" -o "$ICNS"
fi

echo "→ bundle"
APP="$ROOT/build/VPN Nova.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Nova"
cp "$SB" "$APP/Contents/Resources/sing-box"
cp -R Resources/Fonts "$APP/Contents/Resources/Fonts"
cp -R "$SETS" "$APP/Contents/Resources/rule-sets"
cp Resources/install.sh Resources/novactl Resources/com.nova.vpn.core.plist "$APP/Contents/Resources/"
cp "$GUARD_BIN" "$APP/Contents/Resources/novaguard"
chmod 755 "$APP/Contents/Resources/install.sh" "$APP/Contents/Resources/novactl" "$APP/Contents/Resources/sing-box" "$APP/Contents/Resources/novaguard"
cp "$ICNS" "$APP/Contents/Resources/AppIcon.icns"
# sing-box is GPL-3.0: ship its license; the patched source is App/Patches + Scripts/build-core.sh.
mkdir -p "$APP/Contents/Resources/Licenses"
cp "$ROOT/vendor/sing-box-src/LICENSE" "$APP/Contents/Resources/Licenses/sing-box-GPL-3.0.txt"
cp Resources/Fonts/OFL-*.txt "$APP/Contents/Resources/Licenses/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>VPN Nova</string>
    <key>CFBundleDisplayName</key><string>VPN Nova</string>
    <key>CFBundleIdentifier</key><string>com.nova.vpn.app</string>
    <key>CFBundleExecutable</key><string>Nova</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NovaSingBoxVersion</key><string>$SB_VERSION</string>
    <key>NovaServiceVersion</key><string>$SERVICE_VERSION</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP"
echo "✓ $APP ($VERSION, служба $SERVICE_VERSION, sing-box $SB_VERSION)"

if [ "${1:-}" = "--install" ]; then
    DEST="/Applications/VPN Nova.app"
    pkill -x Nova 2>/dev/null || true
    if [ -e "$DEST" ] && [ "$(stat -f %u "$DEST")" = 0 ]; then
        # Installed app is root-owned (protection against tampering): replacing it needs the admin password.
        echo "→ /Applications/VPN Nova.app принадлежит root — нужен пароль администратора"
        osascript -e "do shell script \"rm -rf '$DEST' && ditto '$APP' '$DEST' && chown -R root:wheel '$DEST' && chmod -R go-w '$DEST'\" with administrator privileges with prompt \"Обновление VPN Nova\""
    else
        rm -rf "$DEST"
        cp -R "$APP" "$DEST"
    fi
    echo "✓ $DEST"
fi
