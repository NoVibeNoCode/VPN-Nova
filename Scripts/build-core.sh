#!/bin/bash
# Builds the sing-box core from the official source tag plus Nova's patches → vendor/sing-box.
#
# Why not the official binary: sing-box 1.14.2's REALITY client strips the X25519MLKEM768 key share
# from the ClientHello. Xray 26 servers then don't recognise the client ("reality verification failed"),
# while the official Xray client connects fine. App/Patches/sing-box-reality-mlkem.patch makes the
# client behave like Xray. See Docs/Решения.md.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SB_TAG="v1.14.2"
NOVA_VERSION="1.14.2-nova.1"
GO_VERSION="go1.27.1"
TAGS="with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_acme,with_clash_api"

VENDOR="$ROOT/vendor"
GO="$VENDOR/go/bin/go"
mkdir -p "$VENDOR"

if [ ! -x "$GO" ] || ! "$GO" version | grep -q "$GO_VERSION "; then
    echo "→ Go $GO_VERSION"
    rm -rf "$VENDOR/go"
    curl -fsSL -m 300 "https://go.dev/dl/$GO_VERSION.darwin-arm64.tar.gz" | tar xz -C "$VENDOR"
fi

SRC="$VENDOR/sing-box-src"
if [ ! -d "$SRC/.git" ] || [ "$(git -C "$SRC" describe --tags --exact-match 2>/dev/null)" != "$SB_TAG" ]; then
    echo "→ sing-box $SB_TAG (исходники)"
    rm -rf "$SRC"
    git clone -q --depth 1 --branch "$SB_TAG" https://github.com/SagerNet/sing-box.git "$SRC"
fi

echo "→ патчи"
git -C "$SRC" checkout -q -- .
for p in "$ROOT"/App/Patches/sing-box-*.patch; do
    git -C "$SRC" apply "$p"
    echo "  ✓ $(basename "$p")"
done

export GOPATH="$VENDOR/gopath" GOCACHE="$VENDOR/gocache" GOTOOLCHAIN=local

# `build-core.sh windows-amd64|windows-arm64` cross-compiles the Windows core (wintun is embedded
# in sing-tun, so sing-box.exe is the whole core) → vendor/<target>/sing-box.exe.
if [[ "${1:-}" == windows-* ]]; then
    ARCH="${1#windows-}"
    OUT="$VENDOR/$1/sing-box.exe"
    mkdir -p "$VENDOR/$1"
    echo "→ go build ($1)"
    (cd "$SRC" && GOOS=windows GOARCH="$ARCH" CGO_ENABLED=0 "$GO" build -trimpath -tags "$TAGS" \
        -ldflags "-s -w -X github.com/sagernet/sing-box/constant.Version=$NOVA_VERSION" \
        -o "$OUT" ./cmd/sing-box)
    echo "✓ $OUT ($NOVA_VERSION)"
    exit 0
fi

echo "→ go build"
export CGO_ENABLED=1
(cd "$SRC" && "$GO" build -trimpath -tags "$TAGS" \
    -ldflags "-s -w -X github.com/sagernet/sing-box/constant.Version=$NOVA_VERSION" \
    -o "$VENDOR/sing-box" ./cmd/sing-box) 2>&1 | grep -v 'duplicate libraries' || true
"$VENDOR/sing-box" version | head -1
