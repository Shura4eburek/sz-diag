$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Обязательная приборная проверка "тест реально грузит железо" сразу после старта — не
# отдельный ручной шаг, а жёсткий PASS/FAIL с ненулевым кодом при провале.
#
# Грабля (бэклог п.40, СЗ 160636): OCCTCmd.exe при перенаправленном stdout читает клавишу
# ("Use Q to exit"), сразу получает EOF и тихо умирает за ~45 с — ни строки в логах, ни
# события в Application, CPU 3%. Без сверки с сенсорами это выглядит как «тест идёт», и без
# этой проверки можно было бы «гонять» 6 часов пустоты. `check-load.ps1` уже умеет читать
# CSV глазами (текущее/среднее/макс) — этот рецепт формализует то же самое в вердикт для
# скриптового вызова сразу после `start-occt.ps1` (или любого другого лаунчера — FurMark/TM5
# могут виснуть на той же логике ожидания клавиши).
#
# Использование: запустить тест (start-occt.ps1 и т.п.), затем
#   szcli exec <СЗ> -f tools\recipes\client\confirm-load-or-fail.ps1 --timeout 120
# Код возврата: 0 — нагрузка подтверждена, 1 — нет (CSV не найден/пуст/ниже порога).

$WaitSec = 60   # пауза после старта теста перед проверкой — раньше 3D-сцена может стоять на нуле
$MinPct  = 90   # порог CPU/GPU, ниже которого считаем "нагрузки нет"

Start-Sleep -Seconds $WaitSec

# Путь не зашит: `szcli sensors start` кладёт CSV в ProgramData под именем с номером СЗ,
# ручной lhmmon — в C:\OCCT. Берём самый свежий из обоих мест (та же логика, что в
# check-load.ps1), иначе рецепт может молча читать позавчерашний файл.
$Csv = Get-ChildItem 'C:\ProgramData\szdiag\sensors\*.csv', 'C:\OCCT\sensors.csv' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $Csv) {
    "FAIL: CSV сенсоров не найден — наблюдатель не запущен (нужен szcli sensors start ДО старта теста)."
    exit 1
}
"файл: $Csv"

# I-20 (ревью волны 2): наблюдатель голодает под нагрузкой (SensorWatcher это документирует,
# бэклог п.206) и может умереть, оставив CSV с последней строкой на 100% многочасовой давности —
# без проверки свежести это ложный PASS ровно там, где ложный FAIL правил C-7. Если файл не
# обновлялся дольше 2×$WaitSec, считаем наблюдение оборвавшимся, а не подтверждённым.
$age = (Get-Date) - (Get-Item $Csv).LastWriteTime
if ($age.TotalSeconds -gt (2 * $WaitSec)) {
    "FAIL: CSV не обновлялся {0:N0} с (порог {1} с) — наблюдатель, похоже, умер, данные устарели." -f $age.TotalSeconds, (2 * $WaitSec)
    exit 1
}

# C-7 (ревью волны 2): ConvertFrom-Csv по умолчанию режет по запятой, а штатный наблюдатель
# `szcli sensors start` пишет `;` (SensorWatcher/SensorReport.ParseAny) — с неверным
# разделителем вся шапка склеивается в одну колонку, Get-Val ничего не находит, и рецепт
# выдаёт FAIL при 100% CPU/GPU (воспроизведено на реальной шапке под PS 5.1). Разделитель
# определяем по шапке — та же логика, что уже в sensors-peek.ps1.
$head = Get-Content $Csv -TotalCount 1
$delim = if (($head -split ';').Count -gt ($head -split ',').Count) { ';' } else { ',' }

$rows = (@(Get-Content $Csv -TotalCount 1) + @(Get-Content $Csv -Tail 5)) | ConvertFrom-Csv -Delimiter $delim
if (-not $rows -or $rows.Count -eq 0) { "FAIL: CSV пуст."; exit 1 }
$last = $rows[-1]
$cols = $last.PSObject.Properties.Name

function Get-Val($pattern) {
    foreach ($c in $cols) {
        if ($c -match $pattern -and $last.$c) {
            $v = 0.0
            if ([double]::TryParse($last.$c, [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$v)) { return $v }
        }
    }
    return $null
}

# Узкий наблюдатель `szcli sensors` (cpu_pct/gpu_pct) и широкий CSV lhmmon (Load|CPU Total /
# Load|GPU Core) называют колонки по-разному — рецепт не должен зависеть от того, какой из
# двух наблюдателей поднят.
$cpu = Get-Val 'cpu_pct|Load\|CPU Total'
$gpu = Get-Val 'gpu_pct|Load\|GPU Core'

"CPU: {0}" -f $(if ($null -ne $cpu) { "$cpu %" } else { 'нет данных' })
"GPU: {0}" -f $(if ($null -ne $gpu) { "$gpu %" } else { 'нет данных' })

if (($cpu -ge $MinPct) -or ($gpu -ge $MinPct)) {
    "PASS: нагрузка подтверждена приборно (порог $MinPct%) через $WaitSec с после старта — тест реально идёт."
    exit 0
}
"FAIL: нагрузки нет через $WaitSec с после старта (CPU/GPU ниже $MinPct%) — тест НЕ идёт, не считать что прогон стартовал!"
exit 1
