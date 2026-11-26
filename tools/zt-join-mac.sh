#!/bin/bash
# Подключить мак как hub-машину к сервисной ZT-сети флота: аналог join-части tools/zt-network.ps1
# (сама сеть и её flow rules создаются там, на ПК). pwsh на маке нет.
#
#   tools/zt-join-mac.sh                  # мак → 10.88.0.2 (ПК — 10.88.0.1)
#   tools/zt-join-mac.sh --list           # только показать участников сети на контроллере
#   tools/zt-join-mac.sh --address 10.88.0.3
#
# URL и токен прокси контроллера, id сети — из secrets/fleet.json (tools/fleet-mac.sh pull).
# Нужен ZeroTier One (zerotier-cli). Повторный запуск безопасен.
# Коды: 0 ок · 2 нет секретов флота · 3 контроллер не отвечает · 4 нет ZeroTier One ·
#       5 адрес не назначился · 6 адрес занят другим узлом · 1 прочее
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
address=10.88.0.2
list_only=false
while [ $# -gt 0 ]; do
    case "$1" in
        --address) address="$2"; shift 2 ;;
        --list) list_only=true; shift ;;
        *) echo "Неизвестный параметр: $1" >&2; exit 1 ;;
    esac
done

fail() { echo "zt-join: $2" >&2; exit "$1"; }
[ -f secrets/fleet.json ] || fail 2 "нет secrets/fleet.json — tools/fleet-mac.sh pull"
fleet_get() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1])).get(sys.argv[2]) or "")' secrets/fleet.json "$1"; }
url="$(fleet_get ztControllerUrl)"; url="${url%/}"
net="$(fleet_get ztNetwork)"
[ -n "$url" ] && [ -n "$net" ] || fail 2 "во флоте нет ztControllerUrl/ztNetwork (tools/zt-network.ps1 + fleet.ps1 push на ПК)"

# Токен — заголовком из файла: в командной строке он виден в списке процессов.
hdr="$(mktemp)"
trap 'rm -f "$hdr"' EXIT
chmod 600 "$hdr"
printf 'Authorization: Bearer %s\n' "$(fleet_get ztControllerToken)" > "$hdr"
ctl() {   # ctl METHOD PATH [JSON]
    if [ $# -ge 3 ]; then
        curl -fsS --max-time 20 -X "$1" -H @"$hdr" -H 'Content-Type: application/json' --data "$3" "$url$2"
    else
        curl -fsS --max-time 20 -X "$1" -H @"$hdr" "$url$2"
    fi
}

ctl GET /status >/dev/null || fail 3 "контроллер $url не отвечает"

members() {
    for m in $(ctl GET "/controller/network/$net/member" | python3 -I -c 'import json,sys; print(" ".join(json.load(sys.stdin)))'); do
        ctl GET "/controller/network/$net/member/$m" | python3 -I -c '
import json,sys; d=json.load(sys.stdin)
print(d.get("id") or d.get("address"), d.get("name") or "-", "authorized" if d.get("authorized") else "НЕ authorized", ",".join(d.get("ipAssignments") or []) or "-")'
    done
}

if $list_only; then
    echo "-- сеть $net, участники (узел · имя · авторизация · адреса):"
    members
    exit 0
fi

command -v zerotier-cli >/dev/null || fail 4 "нет zerotier-cli — поставь ZeroTier One"
node="$(zerotier-cli -j info | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["address"])')"
echo "-- эта машина: узел $node → $address"

# Адрес не должен быть занят другим узлом (ПК — 10.88.0.1).
taken="$(members | awk -v a="$address" -v me="$node" '$1 != me && index(","$4",", ","a",") { print $1" ("$2")" }')"
[ -z "$taken" ] || fail 6 "адрес $address уже у $taken — выбери другой (--address)"

zerotier-cli join "$net" >/dev/null
name="hub-$(scutil --get LocalHostName 2>/dev/null || hostname -s)"
ctl POST "/controller/network/$net/member/$node" \
    "{\"authorized\":true,\"ipAssignments\":[\"$address\"],\"noAutoAssignIps\":true,\"name\":\"$name\"}" >/dev/null

for _ in $(seq 1 60); do
    state="$(zerotier-cli -j listnetworks | python3 -I -c '
import json,sys
for n in json.load(sys.stdin):
    if n["id"] == sys.argv[1]:
        print(n.get("status"), n.get("portDeviceName"), ",".join(n.get("assignedAddresses") or []))' "$net")"
    case " $state" in *" OK "*"$address/"*) echo "-- подключено: $state"; break ;; esac
    sleep 2
done
case " $state" in *" OK "*"$address/"*) ;; *) fail 5 "адрес $address не назначился ($state)" ;; esac

# Сеть публичная: участником может стать кто угодно, а flow rules порты hub не режут. На маке
# входящее держат LocalAddressFilter hub (DenySubnets/ZT) и фаервол macOS.
fw="$(/usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate 2>/dev/null || true)"
case "$fw" in *enabled*|*"State = 1"*) echo "   фаервол macOS: включён" ;; *) echo "   ВНИМАНИЕ: фаервол macOS выключен — входящее из сервисной сети ничем, кроме hub, не фильтруется" ;; esac
echo "-- участники сети:"
members
