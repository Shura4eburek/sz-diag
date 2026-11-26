#!/bin/bash
# Запуск SzDiag на маке из исходников: собрать нужную часть из текущего кода в dist и запустить.
# Конфиги и данные рядом с exe (сессии Desk, логи, known_hosts) не трогаются, поэтому окно из
# исходников видит те же заявки и тот же hub, что и обычный dist.
#
#   ./run.sh                   собрать hub и Desk и открыть Desk (hub он поднимет сам)
#   ./run.sh desk              собрать и открыть только Desk
#   ./run.sh hub [--restart]   собрать и запустить hub (с туннелем); --restart — остановить идущий
#   ./run.sh cli [аргументы]   собрать szcli и выполнить команду, например: ./run.sh cli list
#   ./run.sh build             полная сборка dist (tools/build-dist-mac.sh): нужна один раз и после
#                              правок агента, апдейтера или конфигов
#   ./run.sh install           поставить SzDiag.app в /Applications и szcli в ~/.local/bin
#                              (--uninstall — снять); после сборки dist повторять не нужно
#   ./run.sh help              эта справка
#
# Первый запуск на чистой машине — ./run.sh build с параметрами (см. tools/build-dist-mac.sh).
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"

dotnet_bin="$(command -v dotnet || true)"
[ -n "$dotnet_bin" ] || dotnet_bin="$HOME/.dotnet/dotnet"
[ -x "$dotnet_bin" ] || { echo "Не найден dotnet SDK (PATH, ~/.dotnet)." >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1

case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    *) rid=osx-x64 ;;
esac

usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; }

# Сборка во временную папку и копирование поверх dist без конфига: упавшая сборка не трогает
# рабочую версию, а appsettings.json из проекта не затирает настоящий (токен, туннель, пути).
publish() {
    local project="$1" out="$2" staging
    if [ ! -f "$out/appsettings.json" ]; then
        echo "Нет $out/appsettings.json — сначала ./run.sh build." >&2
        exit 1
    fi
    staging="$(mktemp -d)"
    echo "-- сборка $project"
    if ! "$dotnet_bin" publish "$project" -c Release -r "$rid" --self-contained \
            -p:PublishSingleFile=true -v q --nologo -o "$staging"; then
        rm -rf "$staging"
        echo "Сборка упала, в $out осталась прежняя версия." >&2
        exit 1
    fi
    rsync -a --exclude 'appsettings*.json' "$staging/" "$out/"
    rm -rf "$staging"
}

hub_pids() { pgrep -x SzDiag.Hub || true; }

stop_hub() {
    local pids
    pids="$(hub_pids)"
    [ -n "$pids" ] || return 0
    echo "-- останавливаю hub (pid $pids)"
    kill $pids
    for _ in $(seq 1 15); do
        [ -z "$(hub_pids)" ] && return 0
        sleep 1
    done
    echo "Hub не остановился за 15 с." >&2
    exit 1
}

cmd="${1:-all}"
[ $# -gt 0 ] && shift
case "$cmd" in
    all)
        # Идущий hub не трогаем: на нём могут висеть агенты. Свежий hub — ./run.sh hub --restart.
        if [ -z "$(hub_pids)" ]; then
            publish src/SzDiag.Hub dist/host/hub
        else
            echo "-- hub уже запущен (pid $(hub_pids)), его сборку пропускаю"
        fi
        publish src/SzDiag.Desk dist/host/desk
        cd dist/host/desk
        exec ./SzDiag.Desk
        ;;
    desk)
        publish src/SzDiag.Desk dist/host/desk
        cd dist/host/desk
        exec ./SzDiag.Desk
        ;;
    hub)
        if [ -n "$(hub_pids)" ]; then
            if [ "${1:-}" != "--restart" ]; then
                echo "Hub уже запущен (pid $(hub_pids)). Перезапуск со свежей сборкой: ./run.sh hub --restart" >&2
                exit 1
            fi
        fi
        publish src/SzDiag.Hub dist/host/hub
        stop_hub
        exec dist/host/start-hub.command
        ;;
    cli)
        publish src/SzDiag.Cli dist/host/cli
        [ $# -gt 0 ] && exec dist/host/szcli "$@"
        echo "szcli собран: dist/host/szcli"
        ;;
    build)
        exec tools/build-dist-mac.sh "$@"
        ;;
    install)
        exec tools/install-mac.sh "$@"
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        echo "Неизвестная команда: $cmd" >&2
        usage
        exit 2
        ;;
esac
