$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Итог прогона OCCT приборно: сколько РЕАЛЬНО шёл тест и были ли ошибки.
#
# Грабли, которые это породили (СЗ 161716, 24.08.2026):
#  - наблюдатель сенсоров под полной нагрузкой пишет 4 строки за 20 минут (бэклог п.206),
#    поэтому «сколько держалась нагрузка» из CSV взять нельзя — единственный честный
#    источник длительности и ошибок остаётся лог самого OCCT;
#  - «задача запущена» и «тест шёл час» — разные утверждения: на протухшей лицензии
#    OCCTCmd выходит сразу, а задача рапортует Last Result 0 (161190);
#  - результаты лежат в Documents ЗАЛОГИНЕННОГО пользователя, а exec работает под SYSTEM —
#    искать надо по владельцу explorer.exe, иначе папка «не найдена» при живых результатах.
#
# #177 / б.221 (161716, 26.08): список «известных папок» (Documents\OCCT, OneDrive\...\OCCT,
# C:\OCCT) один раз уже подводил — реальный прогон лежал в %LOCALAPPDATA%\Temp\OCCT, где
# OCCT ещё и распаковывает движки (GPUUNREAL, CPULINPACK), забивая папку сотнями файлов
# лицензий. Патч именем ОДНОЙ папки не решает вопрос в принципе — OCCT сам знает, куда
# писать, и это может смениться снова. Поэтому ищем не по имени подпапки, а по СОБСТВЕННЫМ
# файлам-сигнатурам OCCT: `report.csv` (посекундный монитор) и `LastMonitoringValues.json`
# (пишется ТОЛЬКО при штатном выходе — если его нет, тест убили, а не он доработал).
# Роутов для поиска остаётся немного (реальные места, где вообще может писать процесс
# пользователя/интерактивной задачи), а вот имя папки внутри них больше не имеет значения.
#
#   szcli exec <СЗ> -f tools\recipes\client\check-occt-result.ps1

$HoursBack = 6                        # какой давности прогоны показывать
$TaskName  = 'szdiag-occtcomb-161716' # задача прогона (под свою СЗ)

# Профиль залогиненного: под SYSTEM $env:USERPROFILE указывает в systemprofile.
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
$searchRoots = @()
if ($expl) {
    $owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
    $userHome = Join-Path 'C:\Users' $owner.User
    $searchRoots += (Join-Path $userHome 'Documents')                  # частый выбор GUI/CLI по умолчанию
    $searchRoots += (Join-Path $userHome 'OneDrive\Documents')         # Documents бывает перенаправлен в OneDrive
    $searchRoots += (Join-Path $userHome 'AppData\Local\Temp')         # 161716: реально OCCT пишет СЮДА
    "пользователь: $($owner.Domain)\$($owner.User)"
}
$searchRoots += 'C:\OCCT'
$searchRoots = @($searchRoots | Where-Object { Test-Path $_ } | Select-Object -Unique)
if (-not $searchRoots) { 'ни одного корня для поиска результатов OCCT не найдено'; return }

$since = (Get-Date).AddHours(-$HoursBack)

# Сигнатура OCCT, а не имя папки: report.csv лежит при любом запуске, LastMonitoringValues.json
# появляется только на чистом финише (--auto-close довёл дело до конца, а не был убит).
$signature = @()
foreach ($root in $searchRoots) {
    $signature += Get-ChildItem $root -Recurse -File -Include 'report.csv', 'LastMonitoringValues.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $since }
}
if (-not $signature) {
    "ни report.csv, ни LastMonitoringValues.json за последние $HoursBack ч не найдено ни в одном из корней:"
    $searchRoots | ForEach-Object { "   $_" }
    'OCCT либо не запускался, либо результат не за этот период — попробуй увеличить $HoursBack.'
    return
}

$runDir = ($signature | Sort-Object LastWriteTime -Descending | Select-Object -First 1).DirectoryName
# R-I2 (ревью волны 1): $finished раньше считался по ВСЕМ корням поиска, а $runDir — по самому
# свежему файлу. Старый LastMonitoringValues.json из прошлого прогона в ДРУГОЙ папке (в
# пределах $HoursBack) заставлял убитый прогон отрапортовать «штатный выход» — фильтруем
# строго по папке последнего прогона.
$finished = [bool]($signature | Where-Object { $_.DirectoryName -eq $runDir -and $_.Name -eq 'LastMonitoringValues.json' })
"== папка последнего прогона: $runDir"

# Фильтр шума: *LICENSE* (лицензии распакованных движков) и сами движки (GPUUNREAL/CPULINPACK —
# папки с exe/dll, а не с результатом теста).
$files = @(Get-ChildItem $runDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -notmatch 'LICENSE' -and
        $_.DirectoryName -notmatch '\\(GPUUNREAL|CPULINPACK)(\\|$)' -and
        $_.LastWriteTime -gt $since
    } | Sort-Object LastWriteTime)
if (-not $files) { '   свежих файлов результата нет (кроме отфильтрованного шума)'; return }

foreach ($f in $files) {
    '   {0,-55} {1,8} б  {2:HH:mm:ss}' -f $f.FullName.Substring($runDir.Length + 1), $f.Length, $f.LastWriteTime
}

# (1) Окно прогона — по разбросу времён файлов: первый записанный — старт, последний — финиш.
# Это честнее, чем «задача запущена в HH:MM»: старт задачи не равен старту нагрузки.
$first = $files[0]; $last = $files[-1]
'== (1) окно прогона'
'   {0:HH:mm:ss} -> {1:HH:mm:ss} ({2:n1} мин)' -f $first.LastWriteTime, $last.LastWriteTime,
    ($last.LastWriteTime - $first.LastWriteTime).TotalMinutes

foreach ($f in $files) {
    if ($f.Extension -eq '.txt' -or $f.Extension -eq '.log') {
        "   --- $($f.Name)"
        Get-Content $f.FullName -Tail 40 | ForEach-Object { '      ' + $_ }
    }
    elseif ($f.Extension -eq '.csv') {
        $n = (Get-Content $f.FullName | Measure-Object -Line).Lines
        "   --- $($f.Name): $n строк"
        Get-Content $f.FullName -Tail 3 | ForEach-Object { '      ' + $_ }
    }
}

# (2) WHEA/ошибки — только по отфильтрованным файлам результата, не по мусору движков.
'== (2) WHEA/ошибки'
$txt = @($files | Where-Object { $_.Extension -eq '.txt' -or $_.Extension -eq '.log' -or $_.Extension -eq '.csv' })
if ($txt) {
    $hits = @($txt | Select-String -Pattern 'error|fail|ошибк|whea' -ErrorAction SilentlyContinue)
    if ($hits) { $hits | Select-Object -First 30 | ForEach-Object { '   ' + $_.Filename + ': ' + $_.Line.Trim() } }
    else { '   не найдено' }
}
else { '   нет текстовых/csv файлов для разбора' }

# (3) Чем закончился — сам факт наличия LastMonitoringValues.json и есть признак «дошёл до конца».
'== (3) чем закончился'
if ($finished) { '   штатный выход: LastMonitoringValues.json записан (тест доработал расписание/--auto-close)' }
else { '   LastMonitoringValues.json НЕ найден — процесс убит/прерван до штатного финиша (или ещё идёт)' }

'== процессы OCCT сейчас'
$p = @(Get-Process OCCTCmd, OCCT -ErrorAction SilentlyContinue)
if ($p) { $p | ForEach-Object { "   $($_.ProcessName) pid=$($_.Id) cpu=$([int]$_.CPU)с" } }
else { '   не запущены — тест завершён или не стартовал' }

"== задача $TaskName"
schtasks /query /tn $TaskName /fo list /v 2>&1 |
    Select-String 'Status|Result|Start Time|Состояние|результат' |
    ForEach-Object { '   ' + $_.Line.Trim() }
