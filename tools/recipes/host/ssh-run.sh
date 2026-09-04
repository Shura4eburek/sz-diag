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
#
# Третья грабля (бэклог п.173, СЗ 161346): `powershell -Command -` разбирает stdin как
# ИНТЕРАКТИВНЫЙ ввод — пустая строка внутри блока (`if {`, `foreach {`, ...) завершает
# конструкцию досрочно, а перенос через backtick ломает выражение. `kp41-where.ps1` печатал
# шапку и заголовок, а цикл не выполнялся вовсе — БЕЗ единой ошибки, просто пустой вывод.
# Три захода ушло на «почему не работает», хотя дело было в способе доставки, а не в скрипте:
# через -EncodedCommand тот же файл отработал сразу. Поэтому теперь:
#   1. комментарии и пустые строки вырезаются ДО кодирования — это же попутно сжимает скрипт
#      (kp41-where.ps1 после вырезания влез в лимит -EncodedCommand: 7420 симв. база64 против
#      10284 без вырезания);
#   2. -EncodedCommand пробуется первым на ужатом виде, в stdin падаем только при перерасходе
#      лимита;
#   3. способ доставки всегда печатается в stderr — пустой вывод должен сразу читаться как
#      подозрение на обрыв разбора, а не как «скрипт ничего не делает».

set -euo pipefail

SCRIPT="${1:?укажи путь к .ps1}"
IP="${2:-${SZ_CLIENT_IP:?укажи IP клиента вторым аргументом или в SZ_CLIENT_IP}}"
KEY="${SZ_SSH_KEY:-secrets/svc_diag_key}"
USER_NAME="${SZ_SSH_USER:-svc-diag}"
LIMIT=7000          # длиннее — заливаем через stdin, а не одной командой

[ -f "$SCRIPT" ] || { echo "нет файла: $SCRIPT" >&2; exit 1; }
[ -f "$KEY" ] || { echo "нет ключа: $KEY (запусти из корня репо или задай SZ_SSH_KEY)" >&2; exit 1; }

ssh_do() { ssh -i "$KEY" -o StrictHostKeyChecking=no -o ConnectTimeout=15 "$USER_NAME@$IP" "$@"; }

# Комментарии (строка целиком начинается с #) и пустые строки — вон. Инлайн-комментарии
# (`код # пояснение`) НЕ трогаем: `#` внутри строкового литерала ('a#b') резать нельзя, а
# отличить его от реального комментария построчным вырезанием без парсера — нельзя тоже.
#
# I-18 (ревью волны 2): построчное вырезание не знало про блочные комментарии (`<# ... #>`)
# и here-string'и (`@'...'@` / `@"..."@`). Блочный комментарий: открывающая строка (начинается
# с `<`) не резалась, а закрывающая `#>` резалась как обычная строка-комментарий — комментарий
# оставался незакрытым (в репо такой файл есть — erp-fetch-raw.ps1). Here-string: `#`-строки и
# пустые строки внутри него — это ДАННЫЕ (например, содержимое конфига), а не комментарии, и
# резать их молча нельзя. Теперь строки внутри блочного комментария/here-string проходят
# насквозь без изменений, а обычное вырезание работает только вне них.
STRIPPED=$(python -c "
import re
import sys
src = open(sys.argv[1], 'rb').read().decode('utf-8-sig')
out = []
state = 'normal'   # normal | block_comment | herestring
here_end = None
herestring_start = re.compile(r'''@(['\"])\s*\$''')
for line in src.splitlines():
    if state == 'block_comment':
        out.append(line)
        if '#>' in line:
            state = 'normal'
        continue
    if state == 'herestring':
        out.append(line)
        if line.startswith(here_end):
            state = 'normal'
        continue
    stripped = line.strip()
    if stripped.startswith('<#') and '#>' not in stripped:
        out.append(line)
        state = 'block_comment'
        continue
    m = herestring_start.search(line)
    if m:
        out.append(line)
        state = 'herestring'
        here_end = m.group(1) + '@'
        continue
    if not stripped or stripped.startswith('#'):
        continue
    out.append(line)
sys.stdout.write('\n'.join(out))
" "$SCRIPT")

B64=$(printf '%s\n' "$STRIPPED" | python -c "
import sys, base64
print(base64.b64encode(sys.stdin.buffer.read().decode('utf-8').encode('utf-16-le')).decode())
")

if [ ${#B64} -le $LIMIT ]; then
    echo "доставка: -EncodedCommand (${#B64} симв. база64 после вырезания комментариев)" >&2
    ssh_do "powershell -NoProfile -EncodedCommand $B64"
    exit $?
fi

# Длинный скрипт даже после вырезания — потоком в stdin. PowerShell читает stdin в
# OEM-кодировке консоли (cp866), поэтому UTF-8 в него слать нельзя: кириллица в выводе
# превращается в мусор. Кодируем в cp866. I-18: без завершающего перевода строки последняя
# строка потока могла остаться недоразобранной тем же классом ошибки, что и #112 — теперь
# printf добавляет `\n`.
echo "доставка: stdin (${#B64} симв. база64 даже после вырезания комментариев — лимит -EncodedCommand $LIMIT превышен)" >&2
printf '%s\n' "$STRIPPED" | python -c "
import sys
sys.stdout.buffer.write(sys.stdin.buffer.read().decode('utf-8').encode('cp866','replace'))
" | ssh_do "powershell -NoProfile -Command -"
