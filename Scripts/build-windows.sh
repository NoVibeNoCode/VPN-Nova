#!/bin/bash
# Builds VPN Nova for Windows on a Mac (cross-compiled, nothing to install system-wide):
#   build/windows/VPN Nova/          ready-to-run folder (Nova.exe, Nova.Service.exe, sing-box.exe, rule sets)
#   build/VPN-Nova-<ver>-win-x64.zip that folder, zipped
# The installer (Windows/Installer/nova.iss) is compiled on Windows with Inno Setup, from that folder.
# Usage: Scripts/build-windows.sh [--test]   (--test also runs the C# self-test here against the macOS core)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/App/VERSION")"
# The Windows service has its own version line: bump it only when Nova.Service or sing-box change.
SERVICE_VERSION="$(tr -d '[:space:]' < "$ROOT/Windows/SERVICE_VERSION")"
SB_VERSION="1.14.2-nova.1"
RID="win-x64"
OUT="$ROOT/build/windows/VPN Nova"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="$ROOT/vendor/dotnet/dotnet"
if [ ! -x "$DOTNET" ]; then
    echo "→ .NET SDK 10 (в vendor/dotnet)"
    mkdir -p "$ROOT/vendor"
    curl -fsSL -m 60 https://dot.net/v1/dotnet-install.sh -o "$ROOT/vendor/dotnet-install.sh"
    bash "$ROOT/vendor/dotnet-install.sh" --channel 10.0 --install-dir "$ROOT/vendor/dotnet" >/dev/null
fi
export DOTNET_ROOT="$ROOT/vendor/dotnet"

# Core: official sing-box source + App/Patches, cross-compiled (wintun is embedded in sing-tun).
SB="$ROOT/vendor/windows-amd64/sing-box.exe"
if [ ! -f "$SB" ] || [ "$ROOT/App/Patches" -nt "$SB" ]; then
    "$ROOT/Scripts/build-core.sh" windows-amd64
fi

# Rule sets shipped with the app (connect never waits for GitHub) — same list as macOS.
SETS="$ROOT/vendor/rule-sets"
mkdir -p "$SETS"
for t in geosite-youtube geosite-google geosite-telegram geosite-openai geosite-anthropic geosite-instagram \
         geosite-facebook geosite-twitter geosite-discord geosite-netflix geosite-spotify geosite-github geosite-cn \
         geoip-cn geoip-ru; do
    [ -s "$SETS/$t.srs" ] && continue
    repo=sing-geosite; [[ $t == geoip-* ]] && repo=sing-geoip
    curl -fsSL -m 30 -o "$SETS/$t.srs" "https://raw.githubusercontent.com/SagerNet/$repo/rule-set/$t.srs" || echo "⚠ не скачался $t"
done

# Icon: the macOS orb icon, packed into .ico (PNG entries) + a PNG for the window.
ICO="$ROOT/build/windows/Nova.ico"
if [ ! -f "$ICO" ] || [ "$ROOT/Scripts/make-icon.swift" -nt "$ICO" ]; then
    echo "→ иконка"
    rm -rf "$ROOT/build/windows/icon.iconset"
    mkdir -p "$ROOT/build/windows"
    swift "$ROOT/Scripts/make-icon.swift" "$ROOT/build/windows/icon.iconset"
    python3 "$ROOT/Scripts/make-ico.py" "$ROOT/build/windows/icon.iconset" "$ICO"
fi
cp "$ROOT/build/windows/icon.iconset/icon_128x128@2x.png" "$ROOT/Windows/Nova.App/Assets/Nova.png"

if [ "${1:-}" = "--test" ]; then
    echo "→ самотест C#-ядра (против macOS-сборки sing-box)"
    [ -x "$ROOT/vendor/sing-box" ] || "$ROOT/Scripts/build-core.sh"
    "$DOTNET" build -v q "$ROOT/Windows/Nova.Service" >/dev/null
    "$DOTNET" run --project "$ROOT/Windows/Nova.SelfTest" -- "$ROOT/vendor/sing-box" \
        --service "$ROOT/Windows/Nova.Service/bin/Debug/net10.0/Nova.Service"
fi

PROPS=(-p:NovaVersion="$VERSION" -p:NovaServiceVersion="$SERVICE_VERSION" -p:NovaSingBoxVersion="$SB_VERSION"
       -p:Version="${VERSION%%-*}" -p:FileVersion="${VERSION%%-*}.0")
publish() {
    echo "→ dotnet publish $1"
    "$DOTNET" publish "$ROOT/Windows/$1" -c Release -r "$RID" --self-contained \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
        "${PROPS[@]}" -o "$ROOT/build/windows/publish/$1" -v q
}
rm -rf "$ROOT/build/windows/publish"
publish Nova.App
publish Nova.Service
publish Nova.SelfTest

echo "→ папка"
rm -rf "$OUT"
mkdir -p "$OUT/Licenses" "$OUT/Tests"
cp "$ROOT/build/windows/publish/Nova.App/Nova.exe" "$OUT/"
cp "$ROOT/build/windows/publish/Nova.Service/Nova.Service.exe" "$OUT/"
cp "$SB" "$OUT/sing-box.exe"
cp -R "$SETS" "$OUT/rule-sets"
cp "$ROOT/build/windows/publish/Nova.SelfTest/Nova.SelfTest.exe" "$OUT/Tests/"
cp "$ROOT/Windows/Tests/"*.ps1 "$OUT/Tests/"
# sing-box is GPL-3.0: ship its license; the patched source is App/Patches + Scripts/build-core.sh.
cp "$ROOT/vendor/sing-box-src/LICENSE" "$OUT/Licenses/sing-box-GPL-3.0.txt"
cp "$ROOT/Windows/Nova.App/Assets/Fonts/"OFL-*.txt "$OUT/Licenses/"
echo "$VERSION" > "$OUT/VERSION.txt"

ZIP="$ROOT/build/VPN-Nova-$VERSION-win-x64.zip"
rm -f "$ZIP"
(cd "$ROOT/build/windows" && zip -qry "$ZIP" "VPN Nova")
echo "✓ $OUT"
echo "✓ $ZIP ($(du -h "$ZIP" | cut -f1); приложение $VERSION, служба $SERVICE_VERSION, sing-box $SB_VERSION)"
