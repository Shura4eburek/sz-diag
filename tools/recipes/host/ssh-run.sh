#!/bin/bash
# Выполнить локальный .ps1 на клиенте ЧЕРЕЗ SSH, минуя канал агента.
#
# Грабля, которая его породила (СЗ 160705, 12.08.2026, бэклог п.144): после воспроизведения
# дефекта и ручного включения агент поднялся, слал heartbeat и показывался в `szcli list` как
# online — но не выполнял НИЧЕГО. `exec` — таймаут, `pull` — «не закончил забор файлов»
# (папка в pulled\ пустая), `agent restart` — тот же таймаут, потому что идёт тем же залипшим
# каналом. На клиенте в этот момент лежал единственный экземпляр ряда lhmmon с последними
# секундами перед отказом. Достали только так.
#
# Почему -EncodedCommand, а не просто ssh "powershell -Command ...":
#   - кавычки и $-переменные не переживают путь bash -> ssh -> cmd -> powershell;
#   - кириллица в комментариях/выводе бьётся о кодировки;
#   - ConPTY-stdin на портативном sshd под SYSTEM для заливки файла непригоден (виснет на EOF
#     даже на 33 КБ), то есть «скопировать скрипт и запустить» — не вариант.
#
# Вторая грабля (там же): у cmd лимит длины строки ~8191 символов, а base64 от UTF-16LE растит
# скрипт втрое — рецепт на 5 КБ уже даёт `The command line is too long`. Поэтому длинные скрипты
# идут через stdin (`powershell -Command -`).
#
# Про stdin отдельно: заливка ФАЙЛА через ConPTY-stdin действительно виснет (это записано в
# CLAUDE.md и подтверждалось на 33 КБ), но `powershell -Command -` с потоком в 5–10 КБ
# отрабатывает нормально — проверено на 160705. Заливка чанками через Add-Content, которую
# пробовали до этого, не сработала вовсе: файл на клиенте не создавался.
#
# Использование:
#   bash tools/recipes/host/ssh-run.sh <скрипт.ps1> [IP клиента]
#   SZ_CLIENT_IP=192.168.94.85 bash tools/recipes/host/ssh-run.sh client/foo.ps1
#
# Вывод PowerShell по SSH приходит с CLIXML-шумом (прогресс-записи) — фильтровать так:
#   bash ssh-run.sh script.ps1 | grep -v "CLIXML\|<Objs"

set -euo pipefail

SCRIPT="${1:?укажи путь к .ps1}"
IP="${2:-${SZ_CLIENT_IP:?укажи IP клиента вторым аргументом или в SZ_CLIENT_IP}}"
KEY="${SZ_SSH_KEY:-secrets/svc_diag_key}"
USER_NAME="${SZ_SSH_USER:-svc-diag}"
LIMIT=7000          # длиннее — заливаем файлом, а не одной командой

[ -f "$SCRIPT" ] || { echo "нет файла: $SCRIPT" >&2; exit 1; }
[ -f "$KEY" ] || { echo "нет ключа: $KEY (запусти из корня репо или задай SZ_SSH_KEY)" >&2; exit 1; }

ssh_do() { ssh -i "$KEY" -o StrictHostKeyChecking=no -o ConnectTimeout=15 "$USER_NAME@$IP" "$@"; }

B64=$(python -c "
import sys, base64
src = open(sys.argv[1], 'rb').read().decode('utf-8-sig')
print(base64.b64encode(src.encode('utf-16-le')).decode())
" "$SCRIPT")

if [ ${#B64} -le $LIMIT ]; then
    ssh_do "powershell -NoProfile -EncodedCommand $B64"
    exit $?
fi

# Длинный скрипт — потоком в stdin. PowerShell читает stdin в OEM-кодировке консоли (cp866),
# поэтому UTF-8 в него слать нельзя: кириллица в выводе превращается в мусор. Кодируем в cp866.
echo "скрипт длинный (${#B64} симв. base64) — гоню через stdin" >&2
python -c "
import sys
sys.stdout.buffer.write(open(sys.argv[1],'rb').read().decode('utf-8-sig').encode('cp866','replace'))
" "$SCRIPT" | ssh_do "powershell -NoProfile -Command -"
