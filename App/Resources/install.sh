#!/bin/bash
# VPN Nova installer for the root part. Runs as root from the admin password prompt.
# Usage: install.sh <user> <app resources dir> <service version>
set -euo pipefail

USER_NAME="$1"
RES="$2"
VERSION="$3"

DATA="/Library/Application Support/Nova"
HELPER_DIR="/Library/PrivilegedHelperTools"
CTL="$HELPER_DIR/com.nova.vpn.novactl"
GUARD="$HELPER_DIR/com.nova.vpn.novaguard"
LOGS="/Library/Logs/Nova"
LABEL="com.nova.vpn.core"

id "$USER_NAME" >/dev/null

# macOS ships `#includedir /private/etc/sudoers.d`; without it the rule below is ignored.
if ! grep -Eq '^[#@]includedir[[:space:]]+(/private)?/etc/sudoers.d' /etc/sudoers; then
    echo "В /etc/sudoers нет includedir /private/etc/sudoers.d — правило для VPN Nova не подхватится." >&2
    exit 1
fi

if launchctl print "system/$LABEL" >/dev/null 2>&1; then
    launchctl bootout "system/$LABEL" || true
fi

install -d -o root -g wheel -m 755 "$DATA" "$HELPER_DIR" "$LOGS"
# cache.db maps FakeIP addresses to domains: root only.
install -d -o root -g wheel -m 700 "$DATA/work"
chmod 700 "$DATA/work"
find "$DATA/work" -type f -exec chmod 600 {} + 2>/dev/null || true

install -o root -g wheel -m 755 "$RES/sing-box" "$DATA/sing-box"
install -o root -g wheel -m 755 "$RES/novactl" "$CTL"
install -o root -g wheel -m 755 "$RES/novaguard" "$GUARD"
install -o root -g wheel -m 644 "$RES/$LABEL.plist" "$DATA/$LABEL.plist"
echo "$USER_NAME" > "$DATA/owner"
chmod 644 "$DATA/owner"

# Logs from older versions were world-readable and listed visited sites: start clean, root-only + owner ACL.
rm -f "$LOGS/sing-box.log" "$LOGS/sing-box.log.1"
: > "$LOGS/sing-box.log"
chown root:wheel "$LOGS/sing-box.log"
chmod 600 "$LOGS/sing-box.log"
chmod +a "user:$USER_NAME allow read" "$LOGS/sing-box.log"

# The core may only be driven through novactl, without a password, by this user.
TMP="$(mktemp)"
printf '%s ALL=(root) NOPASSWD: %s\n' "$USER_NAME" "$CTL" > "$TMP"
visudo -cf "$TMP" >/dev/null
install -o root -g wheel -m 440 "$TMP" /etc/sudoers.d/nova-vpn
rm -f "$TMP"

# The app bundle the service is installed from becomes root-owned, so nothing running as the
# user can swap sing-box or these scripts before the next "Обновить службу". Updating the app
# then also needs the admin password.
APP="$(cd "$RES/../.." && pwd)"
if [[ "$APP" == /Applications/*.app ]]; then
    chown -R root:wheel "$APP"
    chmod -R go-w "$APP"
fi

echo "$VERSION" > "$DATA/installed-version"
