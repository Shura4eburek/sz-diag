#!/bin/bash
# Залить файл НА клиента по SSH чанками base64 — аварийный транспорт на случай, когда
# `szcli push`/SMB недоступны (см. п.215/216). Направление, обратное ssh-pull-file.sh.
#
# Грабля, которая его породила (СЗ 161346, 26.08.2026, бэклог п.216): пока exec-канал лежал,
# рецепт заливали на клиента кусками по 4000 симв. base64 через
# `powershell -EncodedCommand ... Add-Content ... -NoNewline`. Итог: на клиенте оказалось
# 482 байта вместо 15 482 — доехал только последний, самый короткий кусок. Причина: sshd
# запускает команду через cmd.exe с лимитом строки 8191 символ, а `-EncodedCommand`
# раздувает строку примерно в 2.7 раза (UTF-16 + собственный base64) — чанк 4000 давал
# командную строку ~10.8 КБ и обрубался БЕЗ единого сообщения об ошибке (код возврата
# нормальный, файл "на месте" — просто неполный).
#
# Фикс:
#   1. чанк base64 идёт напрямую текстовым аргументом в `-Command`, БЕЗ `-EncodedCommand` —
#      без множителя 2.7х. Потолок чанка ≤2500 симв. base64 держит итоговую командную строку
#      далеко от лимита cmd.exe 8191 даже с учётом обвязки PowerShell-вызова.
#   2. sha256 СВЕРЯЕТСЯ ОБЯЗАТЕЛЬНО после сборки на клиенте — "команда выполнилась" неотличимо
#      от "файл обрублен" без явной проверки байт в байт (это и было ложью в исходном инциденте).
#
# Использование:
#   bash tools/recipes/host/ssh-push-file.sh <локальный файл> 'C:\путь\на\клиенте\файл.ext' <IP>
#   SZ_CLIENT_IP=192.168.94.85 bash tools/recipes/host/ssh-push-file.sh dump.zip 'C:\OCCT\dump.zip'

set -euo pipefail

LOCAL="${1:?укажи локальный файл}"
REMOTE="${2:?укажи путь назначения на клиенте (Windows-путь)}"
IP="${3:-${SZ_CLIENT_IP:?укажи IP клиента третьим аргументом или в SZ_CLIENT_IP}}"
KEY="${SZ_SSH_KEY:-secrets/svc_diag_key}"
USER_NAME="${SZ_SSH_USER:-svc-diag}"
CHUNK="${SZ_PUSH_CHUNK:-2500}"   # симв. base64 за один SSH-вызов — не поднимать без пересчёта лимита cmd.exe (8191)

[ -f "$LOCAL" ] || { echo "нет файла: $LOCAL" >&2; exit 1; }
[ -f "$KEY" ]   || { echo "нет ключа: $KEY (запусти из корня репо или задай SZ_SSH_KEY)" >&2; exit 1; }
if [ "$CHUNK" -gt 2500 ]; then
    echo "ОШИБКА: SZ_PUSH_CHUNK=$CHUNK превышает проверенный потолок 2500 симв. base64 — именно на 4000 сломалось на 161346" >&2
    exit 1
fi

ssh_do() { ssh -i "$KEY" -o StrictHostKeyChecking=no -o ConnectTimeout=15 "$USER_NAME@$IP" "$@"; }

SHA_LOCAL=$(sha256sum "$LOCAL" | cut -d' ' -f1)
SIZE_LOCAL=$(wc -c < "$LOCAL")
B64=$(python -c "
import sys, base64
print(base64.b64encode(open(sys.argv[1], 'rb').read()).decode())
" "$LOCAL")
TOTAL=${#B64}
TMP_REMOTE="${REMOTE}.b64tmp"

if [ "$TOTAL" -eq 0 ]; then
    echo "ОШИБКА: локальный файл пуст ($LOCAL) — заливать нечего" >&2
    exit 1
fi

CHUNKS=$(( (TOTAL + CHUNK - 1) / CHUNK ))
echo "файл: $LOCAL ($SIZE_LOCAL байт, sha256=$SHA_LOCAL) -> $REMOTE" >&2
echo "base64: $TOTAL симв., $CHUNKS чанков по <=$CHUNK симв." >&2

# Чистый старт: сносим огрызки прошлой попытки — иначе конкатенация продолжит чужой файл
# и sha256 не сойдётся по совершенно непонятной причине.
ssh_do "powershell -NoProfile -Command \"Remove-Item -LiteralPath '$TMP_REMOTE' -Force -ErrorAction SilentlyContinue\""

i=0
n=0
while [ "$i" -lt "$TOTAL" ]; do
    part="${B64:$i:$CHUNK}"
    n=$((n + 1))
    ssh_do "powershell -NoProfile -Command \"[IO.File]::AppendAllText('$TMP_REMOTE', '$part')\""
    i=$((i + CHUNK))
    echo "  чанк $n/$CHUNKS: +${#part} симв. (${i}/${TOTAL})" >&2
done

# Сборка из накопленного base64-текста + сверка sha256 — ОДНИМ вызовом на клиенте, поэтому
# "выполнилось без ошибки" здесь уже означает "прочитал, декодировал, записал, посчитал хеш".
REMOTE_HASH=$(ssh_do "powershell -NoProfile -Command \"\$b64 = Get-Content -LiteralPath '$TMP_REMOTE' -Raw; \$bytes = [Convert]::FromBase64String(\$b64); [IO.File]::WriteAllBytes('$REMOTE', \$bytes); Remove-Item -LiteralPath '$TMP_REMOTE' -Force; (Get-FileHash -LiteralPath '$REMOTE' -Algorithm SHA256).Hash.ToLower()\"")
REMOTE_HASH=$(echo "$REMOTE_HASH" | tr -d '\r\n ')

if [ "$REMOTE_HASH" = "$SHA_LOCAL" ]; then
    echo "OK: sha256 совпал ($REMOTE_HASH) — $REMOTE доехал байт в байт"
    exit 0
else
    echo "ОШИБКА: sha256 НЕ совпал. ожидалось $SHA_LOCAL, получено '$REMOTE_HASH'." >&2
    echo "Файлу на клиенте ($REMOTE) не доверять — вероятен обрыв связи посреди заливки." >&2
    exit 1
fi
