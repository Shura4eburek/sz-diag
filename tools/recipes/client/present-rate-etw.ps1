$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Сколько кадров игра реально ВЫВОДИТ на экран — замер частоты презентов через ETW (СЗ 123456).
#
# Грабля: «FG включён, но FPS не растёт» нельзя разобрать по конфигам и загруженным модулям —
# движок мапит сразу все бэкенды (DLSS-G, FSR-FG, XeSS-FG), а модель dlssd грузится даже при
# выключенном Ray Reconstruction. Нужен выходной fps. PresentMon на клиента не завезён, зато
# ядро DXGI/DxgKrnl шлёт событие на каждый Present — считаем их частоту за N секунд по pid игры.
#
# Что делать с числом: при DLSS-G кадров на выходе примерно вдвое больше отрисованных. Если
# выход упирается в частоту монитора — виноват потолок (V-Sync/лимит), а не FG. Если выход
# равен базовому рендеру — FG не генерирует.
#
#   szcli exec <СЗ> -f tools\recipes\client\present-rate-etw.ps1

$ErrorActionPreference = 'SilentlyContinue'
$seconds = 8
$name    = 'szdiag-present'
$etl     = Join-Path $env:TEMP "$name.etl"
$csv     = Join-Path $env:TEMP "$name.csv"

$proc = Get-Process Stalker2-Win64-Shipping
if (-not $proc) { '   игра не запущена — мерить нечего'; return }
'   цель: pid {0}, замер {1} с' -f $proc.Id, $seconds

logman stop $name -ets 2>&1 | Out-Null
Remove-Item $etl, $csv -Force

# Microsoft-Windows-DXGI: событие Present на каждый вывод кадра
$start = logman start $name -p '{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}' 0xffffffffffffffff 4 -ets -o $etl -nb 16 16 -bs 1024 2>&1
if ($LASTEXITCODE -ne 0) { '   logman start: ' + ($start -join ' ') }
Start-Sleep -Seconds $seconds
logman stop $name -ets 2>&1 | Out-Null

if (-not (Test-Path $etl)) { '   трейс не создан'; return }
'   трейс: {0:N0} б' -f (Get-Item $etl).Length

tracerpt $etl -o $csv -of CSV -y 2>&1 | Out-Null
if (-not (Test-Path $csv)) { '   tracerpt не разобрал трейс'; return }

$rows = Get-Content $csv
'   строк в дампе: {0:N0}' -f $rows.Count

# события Present нашего процесса: pid в CSV идёт отдельным полем
$pidHex = '0x{0:x}' -f $proc.Id
$present = $rows | Where-Object { $_ -match 'Present' -and ($_ -match [regex]::Escape($pidHex) -or $_ -match ",\s*$($proc.Id),") }
'   событий Present по игре за {0} с: {1:N0}' -f $seconds, $present.Count
if ($present.Count -gt 0) {
    '   >>> выходная частота кадров: {0:N1} fps' -f ($present.Count / $seconds)
} else {
    '   Present-событий по pid не нашлось — сводка по типам событий в дампе:'
    $rows | ForEach-Object { ($_ -split ',')[0].Trim('"') } | Group-Object | Sort-Object Count -Descending |
        Select-Object -First 15 | ForEach-Object { '      {0,-52} {1:N0}' -f $_.Name, $_.Count }
}

'=== для сравнения: частота монитора и загрузка GPU ==='
Get-CimInstance Win32_VideoController | ForEach-Object { '   рабочий стол: {0}x{1} @ {2} Гц' -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate }
(Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage').CounterSamples |
    Where-Object CookedValue -gt 5 | Sort-Object CookedValue -Descending | Select-Object -First 3 |
    ForEach-Object { '   GPU 3D: {0:N1}%' -f $_.CookedValue }

Remove-Item $etl, $csv -Force
