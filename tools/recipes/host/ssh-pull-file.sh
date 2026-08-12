#!/bin/bash
# Забрать файл с клиента ЧЕРЕЗ SSH, минуя `szcli pull`.
#
# Грабля (СЗ 160705, 12.08.2026, бэклог п.144): `szcli pull` встал вместе с exec-каналом
# агента — создал пустую папку в hub\pulled\ и отвалился по таймауту, а на клиенте лежал
# единственный ряд lhmmon с последними секундами перед отказом (651 КБ). Забрали так за секунды.
#
# Направление важно: stdout по ConPTY БЫСТРЫЙ (3 МБ base64 уходили за ~1 с), а вот приём
# файла НА клиента через stdin непригоден — поэтому «забрать» работает, «залить» нет
# (для заливки скрипта см. ssh-run.sh с -EncodedCommand).
#
# Использование:
#   bash tools/recipes/host/ssh-pull-file.sh 'C:\OCCT\sensors.csv' <IP> [куда-сохранить]

set -euo pipefail

REMOTE="${1:?укажи путь к файлу на клиенте}"
IP="${2:-${SZ_CLIENT_IP:?укажи IP клиента вторым аргументом или в SZ_CLIENT_IP}}"
OUT="${3:-$(basename "${REMOTE//\\//}")}"
KEY="${SZ_SSH_KEY:-secrets/svc_diag_key}"
USER_NAME="${SZ_SSH_USER:-svc-diag}"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

ssh -i "$KEY" -o StrictHostKeyChecking=no -o ConnectTimeout=15 "$USER_NAME@$IP" \
    "powershell -NoProfile -Command \"[Convert]::ToBase64String([IO.File]::ReadAllBytes('$REMOTE'))\"" > "$TMP"

# ConPTY подмешивает переносы строк и служебный мусор — чистим по base64-алфавиту,
# иначе декодирование падает на первом же лишнем символе.
python - "$TMP" "$OUT" <<'PY'
import base64, re, sys
raw = open(sys.argv[1], 'rb').read().decode('ascii', 'ignore')
b64 = ''.join(re.findall(r'[A-Za-z0-9+/=]+', raw))
data = base64.b64decode(b64 + '=' * (-len(b64) % 4))
open(sys.argv[2], 'wb').write(data)
print('сохранено: %s (%d байт)' % (sys.argv[2], len(data)))
PY
