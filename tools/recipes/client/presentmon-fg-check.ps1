$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Работает ли генерация кадров: сравнение частоты кадров ДВИЖКА и частоты презентов (СЗ 123456).
#
# Грабля, стоившая неверного вывода: сравнивать MsBetweenPresents с MsBetweenDisplayChange
# БЕСПОЛЕЗНО. Сгенерированный кадр Streamline отдаёт через тот же свопчейн, поэтому при живом FG
# удваиваются ОБА счётчика и отношение остаётся 1.00 — ровно как при выключенном FG.
# Различает только частота кадров приложения: PresentMon пишет MsBetweenAppStart (начало кадра
# движка) и MsBetweenSimulationStart (шаг симуляции). При FG 2x:
#     presents ≈ 2 × app frames  → генерация работает
#     presents ≈ app frames      → НИЧЕГО НЕ ЗНАЧИТ, см. вторую граблю ниже
#
# ВТОРАЯ ГРАБЛЯ, ПРОВЕРЕНО НА ЖИВОЙ ЗАЯВКЕ: этот замер по --process_id сгенерированные кадры
# ТОЖЕ не видит. При работающем DLSS-G он дал 131.5 кадров движка и 131.5 презентов (1.00) —
# неотличимо от выключенного FG. Правду показал оверлей NVIDIA: «DLSS 160 | К/с 80», честные 2x.
# Поэтому вердикт ниже — НЕ доказательство: «работает» им подтвердить можно, «не работает» —
# нельзя. Ориентир для вердикта: строка DLSS в оверлее NVIDIA или счётчик, понимающий FG.
# Полезное, что замер всё же даёт: базовый fps и режим представления (лишняя композиция от
# фильтров видна как «Hardware Composed: Independent Flip» вместо «Hardware: Independent Flip»).
#
# Инструмент: szcli push <СЗ> presentmon (PresentMon 2.5.1, Intel/GameTechDev).
#
#   szcli exec <СЗ> -f tools\recipes\client\presentmon-fg-check.ps1

$ErrorActionPreference = 'SilentlyContinue'
$tool = 'C:\Users\nekit\Desktop\client\tools\presentmon\PresentMon.exe'
$csv  = Join-Path $env:TEMP 'szdiag-presentmon.csv'
$sec  = 10

$proc = Get-Process Stalker2-Win64-Shipping
if (-not $proc) { '   игра не запущена — мерить нечего'; return }
if (-not (Test-Path $tool)) { '   PresentMon не доставлен: szcli push <СЗ> presentmon'; return }
'   pid игры {0}, замер {1} с' -f $proc.Id, $sec

Remove-Item $csv -Force
$argv = @('--process_id', $proc.Id, '--output_file', $csv, '--timed', $sec,
          '--terminate_after_timed', '--stop_existing_session')
$p = Start-Process $tool -ArgumentList $argv -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $env:TEMP 'pm-err.txt')
$null = $p.WaitForExit(($sec + 25) * 1000)
if (-not (Test-Path $csv)) {
    '   CSV не создан. stderr:'
    Get-Content (Join-Path $env:TEMP 'pm-err.txt') | Select-Object -First 12 | ForEach-Object { '      ' + $_ }
    return
}

$rows = Import-Csv $csv
'   кадров в выборке: {0:N0}' -f $rows.Count
if ($rows.Count -lt 10) { return }

function Rate($name) {
    if (-not ($rows[0].PSObject.Properties.Name -contains $name)) { return $null }
    $vals = $rows | ForEach-Object { [double]($_.$name) } | Where-Object { $_ -gt 0 }
    if (-not $vals) { return $null }
    $avg = ($vals | Measure-Object -Average).Average
    if ($avg -le 0) { return $null }
    1000.0 / $avg
}

$present = Rate 'MsBetweenPresents'
$display = Rate 'MsBetweenDisplayChange'
$appFps  = Rate 'MsBetweenAppStart'
$simFps  = Rate 'MsBetweenSimulationStart'

'=== частоты ==='
if ($present) { '   презентов (кадров на выходе):      {0,6:N1} fps' -f $present }
if ($display) { '   смен изображения на дисплее:       {0,6:N1} fps' -f $display }
if ($appFps)  { '   кадров движка (MsBetweenAppStart):  {0,6:N1} fps' -f $appFps }
if ($simFps)  { '   шагов симуляции:                   {0,6:N1} fps' -f $simFps }

'=== вердикт ==='
if ($appFps -and $present) {
    $k = $present / $appFps
    '   презентов на кадр движка: {0:N2}' -f $k
    if ($k -gt 1.5) { '   >>> генерация кадров РАБОТАЕТ (на каждый отрисованный кадр уходит ~{0:N1} на экран)' -f $k }
    else {
        '   отношение ~1.00 — этим замером НЕЛЬЗЯ доказать, что FG выключен:'
        '   сгенерированные кадры он не видит. Смотреть строку DLSS в оверлее NVIDIA.'
    }
} else {
    '   PresentMon не дал частоту кадров движка — вердикт по этой выборке невозможен'
}

'=== режим представления ==='
$rows | Group-Object PresentMode | Sort-Object Count -Descending |
    ForEach-Object { '   {0,-36} {1:N0} кадров' -f $_.Name, $_.Count }

Remove-Item $csv -Force
