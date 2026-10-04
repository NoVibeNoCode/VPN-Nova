#!/bin/bash
# Tests for the root-side scripts (novactl, install.sh) without root:
# copies with system paths redirected into a temp dir, `launchctl`/`visudo`/`dscl`/`chown` stubbed.
# Usage: App/Tests/scripts-test.sh <sing-box>   (needs `swift build --product NovaGuard`)
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
RES="$HERE/../Resources"
SB="$1"
GUARD_BIN="$HERE/../.build/debug/NovaGuard"
[ -x "$GUARD_BIN" ] || (cd "$HERE/.." && swift build --product NovaGuard >/dev/null 2>&1)
T="$(mktemp -d)"
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ✓ $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  ✗ $1"; [ -n "${2:-}" ] && echo "      $2"; }
check() { if eval "$1"; then ok "$2"; else bad "$2" "${3:-}"; fi; }

ME="$(id -un)"
mkdir -p "$T/data/work" "$T/logs" "$T/bin" "$T/helper" "$T/etc/sudoers.d" "$T/home/Library/Application Support/Nova/rule-sets"
cp "$SB" "$T/data/sing-box"
cp "$GUARD_BIN" "$T/helper/novaguard"   # uninstall deletes it: never hand it the real build
echo "$ME" > "$T/data/owner"

cat > "$T/bin/launchctl" <<'S'
#!/bin/bash
echo "$*" >> "$STUB_DIR/launchctl.calls"
case "$1" in
  print) [ -f "$STUB_DIR/loaded" ] || exit 113
         printf 'system/com.nova.vpn.core = {\n\tactive count = 1\n\tstate = running\n\tpid = 4242\n\tendpoints = {\n\t\tstate = active\n\t}\n}\n' ;;
  bootstrap) touch "$STUB_DIR/loaded" ;;
  bootout) rm -f "$STUB_DIR/loaded" ;;
  kickstart) ;;
esac
S
cat > "$T/bin/visudo" <<'S'
#!/bin/bash
grep -Eq '^[A-Za-z0-9._-]+ ALL=\(root\) NOPASSWD: /[^ ]+$' "$2"
S
cat > "$T/bin/dscl" <<S
#!/bin/bash
echo "NFSHomeDirectory: $T/home"
S
cat > "$T/bin/chown" <<'S'
#!/bin/bash
echo "chown $*" >> "$STUB_DIR/chown.calls"
S
chmod +x "$T/bin/"*
export STUB_DIR="$T" PATH="$T/bin:$PATH"

sed -e "s|/Library/Application Support/Nova|$T/data|g" \
    -e "s|/Library/Logs/Nova|$T/logs|g" \
    -e "s|/Library/PrivilegedHelperTools/com.nova.vpn.novaguard|$T/helper/novaguard|g" \
    -e 's|\[ "$(id -u)" = 0 \] \|\| .*|true|' \
    -e "s|/etc/sudoers.d/nova-vpn|$T/etc/sudoers.d/nova-vpn|" \
    "$RES/novactl" > "$T/novactl"
chmod +x "$T/novactl"
CTL="$T/novactl"

SECRET="0123456789abcdef0123"
tun_config() {  # $1: extra experimental json
    cat <<J
{"log":{"level":"warn"},"inbounds":[{"type":"tun","tag":"tun-in","address":["172.19.0.1/30"],"auto_route":true,"strict_route":true,"stack":"mixed"}],
 "outbounds":[{"type":"direct","tag":"direct"}],
 "experimental":{"clash_api":{"external_controller":"127.0.0.1:19190","secret":"$SECRET"$1}}}
J
}

echo "novactl apply (stdin + ConfigGuard)"
echo '{}' > "$T/data/config.json"
tun_config "" > "$T/good.json"
"$CTL" apply < "$T/good.json" >/dev/null 2>&1; rc=$?
check '[ $rc -eq 0 ] && cmp -s "$T/good.json" "$T/data/config.json"' "валидный конфиг со stdin установлен" "rc=$rc"
check '[ "$(stat -f %Lp "$T/data/config.json")" = 600 ]' "права 600"
tun_config ',"external_ui":"/"' > "$T/evil.json"
OUT="$("$CTL" apply < "$T/evil.json" 2>&1)"; rc=$?
check '[ $rc -ne 0 ] && cmp -s "$T/good.json" "$T/data/config.json"' "external_ui отклонён валидатором, старый конфиг цел"
check '[[ "$OUT" == *"novaguard"* ]] && [[ "$OUT" != *"$SECRET"* ]]' "в ошибке нет содержимого конфига" "$OUT"
printf '{"log":{"output":"/etc/zshrc"}}' | "$CTL" apply >/dev/null 2>&1; check '[ $? -ne 0 ]' "log.output отклонён"
echo '{"inbounds":[{"type":"tun","tag":"t"}],"outbounds":[{"type":"direct","tag":"x","tcp_fast_open":"oops"}],"experimental":{"clash_api":{"external_controller":"127.0.0.1:1","secret":"0123456789abcdef"}}}' > "$T/badcheck.json"
OUT="$("$CTL" apply < "$T/badcheck.json" 2>&1)"; rc=$?
check '[ $rc -ne 0 ] && [[ "$OUT" == "novactl: sing-box отклонил конфиг" ]]' "ошибка sing-box check не пересказывает файл" "$OUT"
check '[ ! -e "$T/data/config.next.json" ]' "временный файл убран"
: | "$CTL" apply >/dev/null 2>&1; check '[ $? -ne 0 ]' "пустой stdin отклонён"
check '! grep -q "SUDO_UID\|\$2" "$RES/novactl" || ! grep -q "apply)" "$RES/novactl" || ! sed -n "/apply)/,/;;/p" "$RES/novactl" | grep -q "\$2"' "apply не принимает путь от пользователя"

echo "novactl start/stop/status + права"
: > "$T/launchctl.calls"; rm -f "$T/loaded"
check '[ "$("$CTL" status)" = stopped ]' "status: не загружена → stopped"
chmod 755 "$T/data/work"
"$CTL" start
check 'grep -q "^bootstrap system $T/data/com.nova.vpn.core.plist$" "$T/launchctl.calls"' "start: bootstrap"
check '[ "$(stat -f %Lp "$T/logs/sing-box.log")" = 600 ]' "лог создан с правами 600"
check 'ls -le "$T/logs/sing-box.log" | grep -q "user:$ME allow read"' "у владельца есть доступ на чтение по ACL" "$(ls -le "$T/logs/sing-box.log")"
check '[ "$(stat -f %Lp "$T/data/work")" = 700 ]' "work/ (cache.db) — 700"
"$CTL" start; check 'grep -q "^kickstart -k system/com.nova.vpn.core$" "$T/launchctl.calls"' "повторный start: kickstart -k"
st="$("$CTL" status | tr '\n' ' ')"
check '[ "$st" = "running pid 4242 " ]' "status: running и pid" "$st"
"$CTL" stop; check '[ ! -f "$T/loaded" ]' "stop: bootout"
head -c 6000000 /dev/zero > "$T/logs/sing-box.log"; chmod 644 "$T/logs/sing-box.log"
"$CTL" start
check '[ -f "$T/logs/sing-box.log.1" ] && [ "$(stat -f %Lp "$T/logs/sing-box.log.1")" = 600 ] && [ "$(stat -f %Lp "$T/logs/sing-box.log")" = 600 ]' "ротация: оба файла 600"
"$CTL" stop
"$CTL" frobnicate >/dev/null 2>&1; check '[ $? -eq 2 ]' "неизвестная команда → код 2"
cp "$CTL" "$T/ctl-copy"; chmod +x "$T/ctl-copy"; touch "$T/etc/sudoers.d/nova-vpn"
"$T/ctl-copy" uninstall
check '[ ! -e "$T/data" ] && [ ! -e "$T/logs" ] && [ ! -e "$T/etc/sudoers.d/nova-vpn" ] && [ ! -e "$T/ctl-copy" ]' "uninstall удаляет всё"

echo "install.sh"
I="$T/i"
mkdir -p "$I/apps/VPN Nova.app/Contents/Resources" "$I/etc/sudoers.d" "$I/logs"
R="$I/apps/VPN Nova.app/Contents/Resources"
cp "$SB" "$RES/novactl" "$RES/com.nova.vpn.core.plist" "$R/"; cp "$GUARD_BIN" "$R/novaguard"
printf 'root ALL=(ALL) ALL\n#includedir /private/etc/sudoers.d\n' > "$I/etc/sudoers"
echo "old history: connection to secret-site.com" > "$I/logs/sing-box.log"; chmod 644 "$I/logs/sing-box.log"
mkdir -p "$I/data/work"; echo x > "$I/data/work/cache.db"; chmod 644 "$I/data/work/cache.db"
sed -e "s|/Library/Application Support/Nova|$I/data|g" \
    -e "s|/Library/PrivilegedHelperTools|$I/helper|g" \
    -e "s|/Library/Logs/Nova|$I/logs|g" \
    -e "s|/etc/sudoers.d/nova-vpn|$I/etc/sudoers.d/nova-vpn|g" \
    -e "s| /etc/sudoers;| $I/etc/sudoers;|" \
    -e "s|/Applications/\*.app|$I/apps/*.app|" \
    -e 's|-o root -g wheel ||g' \
    "$RES/install.sh" > "$I/install.sh"
: > "$T/chown.calls"
bash "$I/install.sh" "$ME" "$R" 0.2.0 >/dev/null 2>&1; rc=$?
check '[ $rc -eq 0 ]' "установка проходит" "rc=$rc"
check '[ -x "$I/helper/com.nova.vpn.novaguard" ] && [ -x "$I/helper/com.nova.vpn.novactl" ] && [ -x "$I/data/sing-box" ]' "novaguard, novactl, ядро на местах"
check '[ "$(cat "$I/data/owner")" = "$ME" ]' "владелец службы записан"
check '! grep -q "secret-site" "$I/logs/sing-box.log" && [ "$(stat -f %Lp "$I/logs/sing-box.log")" = 600 ]' "старая история из лога стёрта, лог 600"
check 'ls -le "$I/logs/sing-box.log" | grep -q "user:$ME allow read"' "ACL на чтение для владельца"
check '[ "$(stat -f %Lp "$I/data/work")" = 700 ] && [ "$(stat -f %Lp "$I/data/work/cache.db")" = 600 ]' "work/ 700, cache.db 600"
check 'grep -q "chown -R root:wheel $I/apps/VPN Nova.app" "$T/chown.calls"' "бандл в /Applications становится root" "$(cat "$T/chown.calls")"
check '[ "$(cat "$I/data/installed-version")" = 0.2.0 ]' "версия службы записана"
line="$(cat "$I/etc/sudoers.d/nova-vpn")"
check '[ "$line" = "$ME ALL=(root) NOPASSWD: $I/helper/com.nova.vpn.novactl" ]' "правило sudo: только novactl" "$line"
mkdir -p "$T/elsewhere/Dev.app/Contents/Resources"; cp "$R/"* "$T/elsewhere/Dev.app/Contents/Resources/"
: > "$T/chown.calls"
bash "$I/install.sh" "$ME" "$T/elsewhere/Dev.app/Contents/Resources" 0.2.0 >/dev/null 2>&1
check '! grep -q "chown -R" "$T/chown.calls"' "сборка не из /Applications не трогается"
printf 'root ALL=(ALL) ALL\n' > "$I/etc/sudoers"
bash "$I/install.sh" "$ME" "$R" 1 >/dev/null 2>&1; check '[ $? -ne 0 ]' "нет includedir → отказ"
bash "$I/install.sh" "no-such-user-nova" "$R" 1 >/dev/null 2>&1; check '[ $? -ne 0 ]' "несуществующий пользователь → отказ"
plutil -lint "$RES/com.nova.vpn.core.plist" >/dev/null; check '[ $? -eq 0 ]' "plist службы валиден"
check 'bash -n "$RES/install.sh" && bash -n "$RES/novactl"' "синтаксис скриптов"

rm -rf "$T"
echo; echo "Итого: ✓ $PASS  ✗ $FAIL"
[ $FAIL -eq 0 ]
