#!/bin/bash
# Сборка dist на macOS: хост (hub + szcli + Desk) под мак и клиентский пакет под win-x64.
# Мак как запасной хост на случай, когда основной Windows-бокс недоступен (бэклог п.33a).
#
#   tools/build-dist-mac.sh [--signpost-url https://<свой>.up.railway.app] [--hub-name <имя>] [--publish] \
#       [--kb-remote <git-url>] [--port 5099] [--tools-root <каталог>] [--watchdog-hours 6]
#
# Параметры запоминаются в secrets/mac-dist.env (вне git), повторная сборка запускается без них.
# Домена и Cloudflare Tunnel у hub больше нет (2026-11-26, specs/002): hub доступен по LAN и через
# сервисную ZT-сеть (tools/zt-join-mac.sh), клиент находит его через указатель.
# Токены и ключи — из секретов флота (secrets/fleet.json, tools/fleet-mac.sh pull; нет — скрипт
# зовёт его сам, нужен пароль флота): одинаковые на всех hub, апдейтер из любой сборки принимается
# любым hub. Hub клиент ищет сам: LAN → указатель (zt/lan). --publish — подписанный релиз агента
# в указатель (клиенты вне LAN обновляются только так).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

env_file=secrets/mac-dist.env
mkdir -p secrets
chmod 700 secrets
[ -f "$env_file" ] && . "$env_file"
LEGACY_TOKEN="${TOKEN:-}"   # токен мака до флота: принимается hub как соседский
PUBLISH=false

while [ $# -gt 0 ]; do
    case "$1" in
        --hub-url|--tunnel-name|--access-client-id|--access-client-secret)
            echo "$1 больше не поддерживается: домена и туннеля у hub нет (поиск hub — LAN → указатель)" >&2; exit 2 ;;
        --kb-remote) KB_REMOTE="$2"; shift 2 ;;
        --signpost-url) SIGNPOST_URL="$2"; shift 2 ;;
        --hub-name) HUB_NAME="$2"; shift 2 ;;
        --publish) PUBLISH=true; shift ;;
        --token) echo "--token устарел: токены берутся из секретов флота" >&2; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --tools-root) TOOLS_ROOT="$2"; shift 2 ;;
        --watchdog-hours) WATCHDOG_HOURS="$2"; shift 2 ;;
        *) echo "Неизвестный параметр: $1" >&2; exit 2 ;;
    esac
done

KB_REMOTE="${KB_REMOTE:-}"
PORT="${PORT:-5099}"
TOOLS_ROOT="${TOOLS_ROOT:-$root/client-tools}"
WATCHDOG_HOURS="${WATCHDOG_HOURS:-6}"
SIGNPOST_URL="${SIGNPOST_URL:-}"
SIGNPOST_URL="${SIGNPOST_URL%/}"
HUB_NAME="${HUB_NAME:-mac}"

# Секреты флота — до записи env: tools/fleet-mac.sh переносит прежний TOKEN мака в agent-tokens.txt.
if [ ! -f secrets/fleet.json ]; then
    echo "-- нет secrets/fleet.json — забираю секреты флота из Railway (нужен пароль флота)"
    tools/fleet-mac.sh pull || { echo "build-dist-mac: секреты флота не получены (tools/fleet-mac.sh pull)" >&2; exit 3; }
fi
for f in svc_diag_key svc_diag_key.pub fleet-sign.pub fleet-sign.key fleet.json; do
    [ -f "secrets/$f" ] || { echo "build-dist-mac: в secrets/ нет $f — выполни tools/fleet-mac.sh pull" >&2; exit 3; }
done
fleet_get() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1])).get(sys.argv[2]) or "")' secrets/fleet.json "$1"; }
TOKEN="$(fleet_get agentToken)"
MGMT_TOKEN="$(fleet_get managementToken)"
[ -n "$MGMT_TOKEN" ] || MGMT_TOKEN="$TOKEN"
ZT_NETWORK="$(fleet_get ztNetwork)"
ZT_CONTROLLER_URL="$(fleet_get ztControllerUrl)"
ZT_CONTROLLER_TOKEN="$(fleet_get ztControllerToken)"
[ -n "$TOKEN" ] || { echo "build-dist-mac: в secrets/fleet.json нет agentToken" >&2; exit 3; }

umask 077
cat > "$env_file" <<EOF
KB_REMOTE='$KB_REMOTE'
SIGNPOST_URL='$SIGNPOST_URL'
HUB_NAME='$HUB_NAME'
PORT='$PORT'
TOOLS_ROOT='$TOOLS_ROOT'
WATCHDOG_HOURS='$WATCHDOG_HOURS'
EOF
umask 022

dotnet_bin="$(command -v dotnet || true)"
[ -n "$dotnet_bin" ] || dotnet_bin="$HOME/.dotnet/dotnet"
[ -x "$dotnet_bin" ] || { echo "Не найден dotnet SDK (PATH, ~/.dotnet)." >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1

case "$(uname -m)" in
    arm64) host_rid=osx-arm64 ;;
    *) host_rid=osx-x64 ;;
esac
echo "== sz-diag: сборка dist для $host_rid, клиент win-x64, hub без домена (LAN + ZT + указатель) =="

# Публикация во временную папку, поверх — только после успеха: упавшая сборка не трогает рабочую.
# rsync без --delete сохраняет данные рядом с exe (журналы сессий Desk, known_hosts, логи hub).
publish() {
    local project="$1" rid="$2" out="$3" staging
    staging="$(mktemp -d)"
    "$dotnet_bin" publish "$project" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -v q --nologo -o "$staging"
    mkdir -p "$out"
    rsync -a "$staging/" "$out/"
    rm -rf "$staging"
}

# 1. Windows-бинарники для клиента: качаются один раз (client-tools в .gitignore).
ssh_cache=client-tools/ssh
if [ ! -f "$ssh_cache/sshd.exe" ]; then
    echo "-- качаю портативный OpenSSH"
    tmp="$(mktemp -d)"
    curl -fsSL -o "$tmp/ssh.zip" \
        https://github.com/PowerShell/Win32-OpenSSH/releases/download/v9.5.0.0p1-Beta/OpenSSH-Win64.zip
    unzip -q "$tmp/ssh.zip" -d "$tmp"
    mkdir -p "$ssh_cache"
    cp -R "$tmp/OpenSSH-Win64/" "$ssh_cache/"
    rm -rf "$tmp"
fi
cf_client="$TOOLS_ROOT/cloudflared/cloudflared.exe"
if [ ! -f "$cf_client" ]; then
    echo "-- качаю cloudflared для клиентов (szcli push cloudflared)"
    mkdir -p "$(dirname "$cf_client")"
    curl -fsSL -o "$cf_client" \
        https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe
fi

# 2. Ключ сервиса — из секретов флота (выше): приватный остаётся на хосте, публичный уезжает агенту.

# 3. Публикация. Апдейтер — ниже, после конфига клиента: конфиг вшивается в exe.
echo "-- публикую hub / cli / desk / agent"
publish src/SzDiag.Hub "$host_rid" dist/host/hub
publish src/SzDiag.Cli "$host_rid" dist/host/cli
publish src/SzDiag.Desk "$host_rid" dist/host/desk
publish src/SzDiag.Agent win-x64 dist/client

cp secrets/svc_diag_key.pub dist/client/service_key.pub
cp secrets/fleet-sign.pub dist/client/fleet-sign.pub
mkdir -p dist/client/ssh
cp "$ssh_cache"/sshd.exe "$ssh_cache"/ssh-keygen.exe "$ssh_cache"/sftp-server.exe dist/client/ssh/
cp "$ssh_cache"/*.dll dist/client/ssh/ 2>/dev/null || true

# 4. Версия и пакет агента для апдейтера. Те же файлы, что в build-dist.ps1: без appsettings.json,
# tools/ и самого апдейтера.
version="$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d-%H%M%S)"
[ -z "$(git status --porcelain 2>/dev/null)" ] || version="$version-dirty"
printf '%s' "$version" > dist/client/version.txt
# Время сборки — порядок релизов для апдейтера (git sha не упорядочен), защита от replay (specs/002 R6).
build_time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf '{"version":"%s","buildTime":"%s"}' "$version" "$build_time" > dist/client/build-info.json

pkg="$(mktemp -d)"
cp dist/client/SzDiag.Agent.exe dist/client/service_key.pub dist/client/fleet-sign.pub \
    dist/client/version.txt dist/client/build-info.json "$pkg/"
# libzt (ZeroTier) — нативная, в single-file не вшивается: без неё агент работает без ZT.
cp dist/client/libzt.dll "$pkg/" 2>/dev/null || echo "   нет dist/client/libzt.dll — агент из этого пакета будет без ZT"
cp dist/client/testsuite.json "$pkg/" 2>/dev/null || true
cp -R dist/client/ssh "$pkg/ssh"
agent_dist=dist/host/hub/agent-dist
mkdir -p "$agent_dist"
rm -f "$agent_dist/package.zip"
(cd "$pkg" && zip -qr "$root/$agent_dist/package.zip" .)
rm -rf "$pkg"
shasum -a 256 "$agent_dist/package.zip" | cut -d' ' -f1 | tr -d '\n' > "$agent_dist/package.sha256"
printf '%s' "$version" > "$agent_dist/version.txt"
echo "-- пакет апдейтера: $agent_dist/package.zip (version=$version)"

# 5. База знаний: рабочая копия приватного репозитория, бэкапом дальше занимается сам hub.
kb="$root/dist/host/kb"
if [ -n "$KB_REMOTE" ] && [ ! -d "$kb/.git" ]; then
    echo "-- клонирую базу знаний"
    git clone -q "$KB_REMOTE" "$kb"
fi
mkdir -p "$kb"
kb_backup=false
[ -d "$kb/.git" ] && kb_backup=true

# 6. Туннеля hub нет: домен отменён (2026-11-26). Старые ~/.cloudflared/*.yml скрипт не трогает.

# 7. Конфиги. В unquoted-heredoc `\\\\` даёт `\\` — экранированный слеш в JSON.
claude_bin="$(command -v claude || true)"

# Соседские токены агентов: прежний конфиг hub + прежний токен мака + secrets/agent-tokens.txt.
# Личные ZT-сети мака (всё, кроме сервисной) — в DenySubnets: на их адреса hub не отвечает.
zt_nets_json="$(zerotier-cli -j listnetworks 2>/dev/null || echo '[]')"
read -r extra_tokens_json deny_subnets_json < <(
    ZT_NETS="$zt_nets_json" python3 -I - "$TOKEN" "$LEGACY_TOKEN" "$ZT_NETWORK" <<'PY'
import ipaddress, json, os, sys
token, legacy, service_net = sys.argv[1:4]
prev = {}
try:
    prev = json.load(open("dist/host/hub/appsettings.json")).get("Hub", {})
except Exception:
    pass
extra = list(prev.get("ExtraAgentTokens") or [])
if prev.get("AgentToken") not in (None, "", "dev-token"):
    extra.append(prev["AgentToken"])
if legacy and legacy != "dev-token":
    extra.append(legacy)
try:
    for line in open("secrets/agent-tokens.txt"):
        line = line.strip()
        if line and not line.startswith("#"):
            extra.append(line)
except FileNotFoundError:
    pass
extra = [t for t in dict.fromkeys(extra) if t and t != token]
deny = list(prev.get("DenySubnets") or [])
try:
    for n in json.loads(os.environ.get("ZT_NETS") or "[]"):
        if n.get("id") == service_net:
            continue
        for a in n.get("assignedAddresses") or []:
            try:
                net = ipaddress.ip_interface(a).network
            except ValueError:
                continue
            if net.version == 4:
                deny.append(str(net))
except Exception:
    pass
deny = list(dict.fromkeys(d for d in deny if d))
compact = dict(separators=(",", ":"))   # без пробелов: read ниже режет по ним
print(json.dumps(extra, **compact), json.dumps(deny, **compact))
PY
)
echo "-- соседские токены агентов: $(python3 -I -c 'import json,sys; print(len(json.loads(sys.argv[1])))' "$extra_tokens_json")"
echo "-- DenySubnets (личные ZT-сети): $deny_subnets_json"
signpost_enabled=false
[ -n "$SIGNPOST_URL" ] && signpost_enabled=true
zt_enabled=false
[ -n "$ZT_NETWORK" ] && zt_enabled=true
echo "-- указатель: ${SIGNPOST_URL:-выключен (--signpost-url https://<свой>.up.railway.app)}"
if [ -n "$ZT_NETWORK" ]; then echo "-- ZeroTier: сеть $ZT_NETWORK"; else echo "-- ZeroTier: не настроен (tools/zt-network.ps1 на ПК)"; fi

[ -n "$SIGNPOST_URL" ] || echo "   ВНИМАНИЕ: без указателя клиент найдёт hub только в той же LAN (--signpost-url)"
sign_pub_json="$(python3 -I -c 'import json,sys; print(json.dumps(open(sys.argv[1]).read().strip())[1:-1])' secrets/fleet-sign.pub)"

cat > dist/host/hub/appsettings.json <<EOF
{
  "Urls": "http://0.0.0.0:$PORT",
  "Hub": {
    "AgentToken": "$TOKEN",
    "ExtraAgentTokens": $extra_tokens_json,
    "ManagementToken": "$MGMT_TOKEN",
    "Port": $PORT,
    "SqliteConnectionString": "Data Source=$root/dist/host/szdiag.db",
    "KnowledgeBaseRoot": "$kb",
    "AgentDistRoot": "$root/$agent_dist",
    "ToolsRoot": "$TOOLS_ROOT",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15",
    "KbBackup": {
      "Enabled": $kb_backup,
      "Interval": "00:15:00",
      "Remote": "origin",
      "Branch": "main",
      "CommandTimeout": "00:02:00"
    },
    "Tunnel": {
      "Enabled": false
    },
    "Signpost": {
      "Enabled": $signpost_enabled,
      "Url": "$SIGNPOST_URL",
      "HubName": "$HUB_NAME",
      "SignKeyPath": "$root/secrets/fleet-sign.key",
      "PublishInterval": "00:00:30",
      "ExtraEndpoints": []
    },
    "DenySubnets": $deny_subnets_json,
    "ZeroTier": {
      "Enabled": $zt_enabled,
      "Network": "$ZT_NETWORK",
      "Subnet": "10.88.0.0/24",
      "SweepAfter": "00:10:00",
      "ControllerUrl": "$ZT_CONTROLLER_URL",
      "ControllerToken": "$ZT_CONTROLLER_TOKEN",
      "HubAddresses": ["10.88.0.1", "10.88.0.2"]
    }
  }
}
EOF

cat > dist/host/cli/appsettings.json <<EOF
{
  "HubBaseUrl": "http://localhost:$PORT",
  "ManagementToken": "$MGMT_TOKEN",
  "KbRoot": "$kb",
  "SshKeyPath": "$root/secrets/svc_diag_key"
}
EOF

cat > dist/host/desk/appsettings.json <<EOF
{
  "HubBaseUrl": "http://localhost:$PORT",
  "ManagementToken": "$MGMT_TOKEN",
  "KbRoot": "$kb",
  "ClaudePath": "$claude_bin",
  "ClaudeWorkDir": "$root",
  "PermissionMode": "auto"
}
EOF

cat > dist/client/appsettings.json <<EOF
{
  "HubUrl": "",
  "AgentToken": "$TOKEN",
  "SignPublicKey": "$sign_pub_json",
  "SignpostUrl": "$SIGNPOST_URL",
  "CloudflaredPath": "tools\\\\cloudflared\\\\cloudflared.exe",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": $WATCHDOG_HOURS,
  "HeartbeatSeconds": 15,
  "StatePath": "C:\\\\ProgramData\\\\szdiag\\\\state.json",
  "TestSuitePath": "testsuite.json"
}
EOF
chmod 600 dist/host/hub/appsettings.json dist/host/cli/appsettings.json dist/host/desk/appsettings.json

# Апдейтер — с вшитым конфигом клиента: на клиент приносится один SzDiag.Updater.exe. Файл-источник
# живёт только на время publish (в .gitignore — внутри токен агента).
embedded_cfg=src/SzDiag.Updater/embedded-client.json
cp dist/client/appsettings.json "$embedded_cfg"
echo "-- публикую SzDiag.Updater -> dist/client (конфиг клиента вшит в exe)"
"$dotnet_bin" publish src/SzDiag.Updater -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -v q --nologo -o dist/client || { rm -f "$embedded_cfg"; exit 1; }
rm -f "$embedded_cfg"

# 8. Лаунчеры. caffeinate: задремавший ноутбук уронил бы связь со всеми агентами.
cat > dist/host/start-hub.command <<'EOF'
#!/bin/bash
cd "$(dirname "$0")/hub" || exit 1
exec /usr/bin/caffeinate -i ./SzDiag.Hub
EOF
cat > dist/host/szcli <<'EOF'
#!/bin/bash
exec "$(dirname "$0")/cli/SzDiag.Cli" "$@"
EOF
cat > dist/host/start-desk.command <<'EOF'
#!/bin/bash
cd "$(dirname "$0")/desk" || exit 1
exec ./SzDiag.Desk
EOF
chmod +x dist/host/start-hub.command dist/host/szcli dist/host/start-desk.command

# 9. Минимум для клиента: апдейтер сам докачает агента с hub. В архиве токен — не выкладывать публично.
rm -f dist/client-bootstrap.zip
(cd dist/client && zip -q ../client-bootstrap.zip SzDiag.Updater.exe appsettings.json)

# Приватные ключи флота на клиента не уезжают (FR-014): клиентский dist и пакет — публичные артефакты.
leaks="$( { find dist/client -type f \( -name fleet-sign.key -o -name svc_diag_key -o -name fleet.json -o -name agent-tokens.txt \);
            unzip -Z1 "$agent_dist/package.zip" | grep -E '(^|/)(fleet-sign\.key|svc_diag_key|fleet\.json|agent-tokens\.txt)$' | sed "s|^|$agent_dist/package.zip!|"; } || true)"
if [ -n "$leaks" ]; then
    echo "build-dist-mac: в клиентский dist/пакет попали секреты флота — удали и пересобери:" >&2
    echo "$leaks" >&2
    exit 7
fi

# Подписанный релиз агента в свой указатель: клиент вне LAN берёт пакет оттуда (specs/002, US1).
if $PUBLISH; then
    [ -n "$SIGNPOST_URL" ] || { echo "build-dist-mac: --publish без указателя бессмыслен — задай --signpost-url" >&2; exit 8; }
    pkg_zip="$agent_dist/package.zip"
    pkg_sha="$(shasum -a 256 "$pkg_zip" | cut -d' ' -f1)"
    rel_json="$(dist/host/cli/SzDiag.Cli fleet sign-release "$pkg_zip" --secrets "$root/secrets" \
        --version "$version" --build-time "$build_time" --url '/package/{sha256}')" \
        || { echo "build-dist-mac: fleet sign-release не прошёл" >&2; exit 8; }
    printf '%s' "$rel_json" > "$agent_dist/release.json"
    echo "-- указатель: загружаю пакет ($(du -m "$pkg_zip" | cut -f1) МБ)"
    rel_header="$(printf '%s' "$rel_json" | base64 | tr -d '\n')"
    curl -fsS -X PUT --max-time 600 -H 'Content-Type: application/zip' -H "X-SzDiag-Release: $rel_header" \
        --data-binary "@$pkg_zip" "$SIGNPOST_URL/package/$pkg_sha" >/dev/null \
        && curl -fsS -X PUT --max-time 60 -H 'Content-Type: application/json' \
            --data-binary "$rel_json" "$SIGNPOST_URL/signpost/release" >/dev/null \
        || { echo "build-dist-mac: указатель не принял релиз" >&2; exit 8; }
    echo "-- указатель: релиз $version опубликован"
elif [ -n "$SIGNPOST_URL" ]; then
    echo "   релиз в указатель НЕ опубликован (без --publish): клиенты вне LAN эту версию не получат"
fi

# 10. Приложение в /Applications и szcli в PATH — чтобы не искать бинарь в dist.
tools/install-mac.sh

echo
echo "== Готово =="
echo "Hub:     dist/host/start-hub.command"
echo "szcli:   dist/host/szcli"
echo "Desk:    dist/host/start-desk.command"
echo "Клиент:  dist/client-bootstrap.zip (распаковать, запустить SzDiag.Updater.exe от администратора)"
