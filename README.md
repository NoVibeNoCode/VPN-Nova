# VPN Nova

Бесплатный VPN-клиент для macOS в строке меню. Разные приложения и сайты идут через разные серверы.

*A free macOS menu-bar VPN client with per-app and per-domain routing, built on sing-box. English summary below.*

## Что умеет

- **Правила по приложениям и доменам.** Telegram → сервер A, банк → сервер B, YouTube → A, китайские сайты и локальная сеть — напрямую, всё остальное — куда выберешь.
- **Порядок правил:** локальная сеть → домены → приложения → «всё остальное». Более точное правило побеждает: «Chrome → B» и «youtube.com → A» дают YouTube через A, остальной Chrome через B.
- **Серверы по ссылкам:** `vless://` (Reality, Vision, ws, grpc, h2, httpupgrade), `vmess://`, `trojan://`, `ss://` (включая SS-2022 и плагины), `hy2://` / `hysteria2://`, `tuic://`. Или сырой JSON outbound'а sing-box.
- **Kill switch:** если сервер упал, его трафик блокируется. Без kill switch — временно идёт напрямую.
- **«Всё остальное»** переключается из меню на лету, без переподключения.
- **Живая карта маршрутов:** какие приложения и сайты сейчас через какой сервер.
- **Наборы правил** (YouTube, Telegram, Китай, OpenAI, Anthropic и др.) встроены в приложение и обновляются в фоне.
- **DNS через FakeIP:** сайты, идущие через сервер, резолвит сам сервер.
- Предупреждает, если параллельно включён другой VPN, который перехватывает трафик.

Требуется macOS 14+ на Apple Silicon.

## Установка

1. Скачай `VPN-Nova-<версия>.dmg` из [Releases](../../releases) и перетащи **VPN Nova** в «Программы».
2. Приложение подписано только ad-hoc (без платного сертификата Apple), поэтому при первом запуске macOS его заблокирует. Открой **Системные настройки → Конфиденциальность и безопасность** и нажми **«Всё равно открыть»**.
3. Щит в строке меню → **«Установить службу»** → пароль администратора. Ставится фоновая служба с ядром sing-box (один раз).
4. **Серверы** → вставь ссылку → **Импорт**. **Правила** → добавь приложения и домены. Включай звездой.

Хочешь проверить, что скачанный файл не подменён — сверь SHA-256 из релиза: `shasum -a 256 VPN-Nova-*.dmg`. Ещё надёжнее — собрать самому (ниже).

## Сборка из исходников

```bash
Scripts/build.sh            # → build/VPN Nova.app
Scripts/make-dmg.sh         # → releases/VPN-Nova-<версия>.dmg
Scripts/test.sh             # самотест и тесты root-скриптов
```

Нужны Xcode или Command Line Tools. Скрипт сам скачает официальный Go с go.dev и исходники sing-box с GitHub.

## Как устроено

| Часть | Что делает |
|---|---|
| `App/Sources/Nova` | Приложение в строке меню (SwiftUI). Работает от пользователя |
| `App/Sources/NovaCore` | Профиль → конфиг sing-box, разбор ссылок, Clash API, kill switch |
| `App/Sources/NovaGuard` | Проверка конфига перед тем, как его применит root |
| `App/Resources/novactl` | Управление ядром от root через `sudo -n` (правило только для этой команды) |
| `App/Resources/install.sh` | Установка службы (с паролем администратора) |
| `App/Patches` | Патч к sing-box (см. ниже) |

**Ядро.** sing-box v1.14.2 собирается из официальных исходников с одним патчем (`App/Patches/sing-box-reality-mlkem.patch`). В Reality-клиенте он оставляет в ClientHello гибридный обмен ключами X25519MLKEM768, как это делают Xray и Chrome. Без него серверы на Xray 26 не принимают клиента: `reality verification failed`.

**Безопасность root-части:**
- Root применяет только конфиг той формы, которую генерирует Nova. `NovaGuard` отклоняет `external_ui`, `log.output`, любые `*_path`, лишние инбаунды и Clash API не на 127.0.0.1.
- Конфиг передаётся через stdin: root не открывает пути, выбранные пользователем.
- Лог и `cache.db` доступны только root и владельцу. По умолчанию лог без доменов.
- После установки службы бандл приложения принадлежит root, чтобы его нельзя было подменить без пароля.

## Ограничения

- Одновременно с другим VPN (Network Extension) работать не будет: он забирает трафик раньше.
- Safari ходит в сеть через общий процесс WebKit — для него используй правила по доменам, не по приложению.
- Правила по IP-наборам (`geoip-*`) срабатывают только для соединений без домена (следствие FakeIP).

## Лицензии

- VPN Nova — **GPL-3.0** (`LICENSE`).
- [sing-box](https://github.com/SagerNet/sing-box) — GPL-3.0, © nekohasekai. В приложение входит ядро, собранное из его исходников с патчем из `App/Patches`.
- Наборы правил — [sing-geosite](https://github.com/SagerNet/sing-geosite) / [sing-geoip](https://github.com/SagerNet/sing-geoip).
- Шрифты Unbounded и Manrope — SIL Open Font License 1.1 (`App/Resources/Fonts`).

Проект не связан с авторами sing-box. Используй в соответствии с законами своей страны.

---

## English

VPN Nova is a free macOS (14+, Apple Silicon) menu-bar client that routes individual **apps** and **domains** through different servers. It runs [sing-box](https://github.com/SagerNet/sing-box) as a root launchd service in TUN mode and controls it through the Clash API.

- Rules: LAN → domains → apps → default; servers from `vless/vmess/trojan/ss/hy2/tuic` links or raw JSON.
- Kill switch, live default-route switching, live routes map, built-in rule sets, FakeIP DNS.
- The core is sing-box v1.14.2 built from source with one patch, so REALITY keeps the X25519MLKEM768 key share (Xray 26 servers reject clients without it).
- Root part hardening: an allowlist (`NovaGuard`) for configs, stdin-only apply, private logs, root-owned app bundle.

Build: `Scripts/build.sh`, package: `Scripts/make-dmg.sh`, test: `Scripts/test.sh`. The app is ad-hoc signed, so allow it in *System Settings → Privacy & Security* on first launch.

License: GPL-3.0.
