$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Подменить расписание, которое возьмёт `szcli test run <СЗ> occt`.
#
# Грабля (СЗ 161346): шаг `occt` в testsuite.json жёстко зашит на `{workdir}\schedule.json`,
# а в раздаче (`Hub.ToolsRoot`) лежит расписание на 5+5 минут — при том что в репо
# `deploy/occt/schedule.json` это 30+30, а `schedule-long.json` 90+90. В итоге «прогон
# запущен» отработал 10 минут, отчёт приехал «успешным», и это легко прочитать как
# «дефект не воспроизводится» — хотя машину толком не грузили. Выбрать другое расписание
# через CLI нельзя, поэтому подменяем файл на клиенте.
#
# Оригинал сохраняется рядом (schedule.json.orig) — вернуть можно этим же скриптом с $Restore.
$Want    = 'schedule-long.json'   # ← какое расписание сделать текущим (лежит рядом, доехало push'ем)
$Restore = $false                 # ← true: вернуть оригинал из .orig

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$dir  = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'
$cur  = Join-Path $dir 'schedule.json'
$orig = "$cur.orig"

if ($Restore) {
    if (-not (Test-Path $orig)) { throw "нет резервной копии $orig" }
    Copy-Item $orig $cur -Force
    Remove-Item $orig -Force
    "восстановлено расписание по умолчанию"
} else {
    $src = Join-Path $dir $Want
    if (-not (Test-Path $src)) { throw "нет файла $src — сначала szcli push <СЗ> occt" }
    # .orig пишем один раз: повторный запуск не должен затереть настоящий оригинал копией длинного.
    if (-not (Test-Path $orig)) { Copy-Item $cur $orig -Force; "оригинал сохранён: $orig" }
    Copy-Item $src $cur -Force
    "подменено: $Want -> schedule.json"
}

# Приёмка: печатаем то, что реально будет выполняться, а не то, что копировали.
$d = Get-Content $cur -Raw | ConvertFrom-Json
$total = [TimeSpan]::Zero
foreach ($p in $d.Periods) {
    $inf = if ($p.IsInfinite) { ' INFINITE' } else { '' }
    "   $($p.TestType)  $($p.Duration)$inf"
    if (-not $p.IsInfinite) { $total += [TimeSpan]::Parse($p.Duration) }
}
"итого по расписанию: $total"
