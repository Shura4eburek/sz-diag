#!/bin/bash
# Ключи флота на маке: аналог `tools/fleet.ps1 pull` (specs/002-domain-independent-access).
# pwsh на маке нет, а build-dist-mac.sh без секретов флота собирает апдейтер с чужим токеном —
# hub на ПК ответил бы ему 401.
#
#   tools/fleet-mac.sh pull      # забрать и расшифровать секреты флота в secrets/
#
# Нужен railway CLI, залогиненный и слинкованный с проектом, где живёт сервис szdiag-signpost
# (railway login; railway link). Пароль флота — запросом без эха или SZDIAG_FLEET_PASSWORD.
# Коды — как у fleet.ps1: 0 ок · 3 нет railway-логина/линка или бандла · 4 неверный пароль ·
# 5 бандл испорчен · 1 прочее.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
service="${SZDIAG_SIGNPOST_SERVICE:-szdiag-signpost}"
secrets="$root/secrets"

fail() { echo "fleet: $2" >&2; exit "$1"; }

[ "${1:-}" = pull ] || { echo "использование: tools/fleet-mac.sh pull" >&2; exit 1; }

command -v railway >/dev/null || fail 3 "railway CLI не найден (brew install railway)"
railway whoami >/dev/null 2>&1 || fail 3 "railway не залогинен: railway login"
vars_json="$(railway variable list --service "$service" --json 2>/dev/null)" \
    || fail 3 "не читаются переменные сервиса $service. Слинкуй проект из корня репо: railway link"
bundle="$(printf '%s' "$vars_json" | python3 -I -c 'import json,sys; print(json.load(sys.stdin).get("SZDIAG_FLEET_BUNDLE",""))')"
[ -n "$bundle" ] || fail 3 "в Railway нет SZDIAG_FLEET_BUNDLE — флот ещё не создан (tools/fleet.ps1 init на ПК)"

dotnet_bin="$(command -v dotnet || true)"
[ -n "$dotnet_bin" ] || dotnet_bin="$HOME/.dotnet/dotnet"
[ -x "$dotnet_bin" ] || fail 1 "не найден dotnet SDK (PATH, ~/.dotnet)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$dotnet_bin")}" DOTNET_CLI_TELEMETRY_OPTOUT=1

# szcli — во временную папку и напрямую: `dotnet run` смешал бы вывод сборки с выводом команды.
cli_out="${TMPDIR:-/tmp}/szdiag-fleet-cli"
echo "-- собираю szcli для работы с ключами флота"
"$dotnet_bin" build src/SzDiag.Cli/SzDiag.Cli.csproj -c Release -o "$cli_out" -v q --nologo >/dev/null \
    || fail 1 "не собрался szcli (dotnet build src/SzDiag.Cli)"

mkdir -p "$secrets"
chmod 700 "$secrets"

# Прежний ssh-ключ мака не теряем: им ещё могут открываться клиенты со старым пакетом.
if [ -f "$secrets/svc_diag_key" ] && [ ! -f "$secrets/fleet.json" ]; then
    stamp="$(date +%Y%m%d-%H%M%S)"
    cp "$secrets/svc_diag_key" "$secrets/svc_diag_key.local-$stamp"
    [ -f "$secrets/svc_diag_key.pub" ] && cp "$secrets/svc_diag_key.pub" "$secrets/svc_diag_key.local-$stamp.pub"
    echo "-- прежний ssh-ключ мака сохранён как svc_diag_key.local-$stamp"
fi

# Прежний токен мака: апдейтеры, уже собранные здесь, несут его — hub флота должен их принимать.
prev_token=""
if [ -f "$secrets/mac-dist.env" ]; then
    prev_token="$( (. "$secrets/mac-dist.env"; printf '%s' "${TOKEN:-}") )"
fi

SZDIAG_FLEET_BUNDLE="$bundle" "$cli_out/SzDiag.Cli" fleet decrypt --secrets "$secrets" || exit $?

if [ -n "$prev_token" ] && [ "$prev_token" != dev-token ]; then
    fleet_token="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["agentToken"])' "$secrets/fleet.json")"
    tokens_file="$secrets/agent-tokens.txt"
    if [ "$prev_token" != "$fleet_token" ] && ! grep -qxF "$prev_token" "$tokens_file" 2>/dev/null; then
        umask 077
        { echo "# прежний токен мака до перехода на флот ($(date +%Y-%m-%d))"; echo "$prev_token"; } >> "$tokens_file"
        echo "-- прежний токен мака добавлен в secrets/agent-tokens.txt (переходный)"
    fi
fi
echo "-- готово: секреты флота в secrets/"
