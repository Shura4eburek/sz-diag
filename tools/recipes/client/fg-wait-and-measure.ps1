$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Дождаться устоявшегося геймплея и замерить генерацию кадров (СЗ 123456).
#
# Зачем ждать: сразу после запуска Сталкер 2 компилирует шейдеры — CPU занят, GPU 3D почти в
# простое, кадры идут рвано. Замер на этой стадии даёт мусор. Ждём, пока GPU 3D устойчиво
# поднимется выше порога (рендер сцены пошёл), и только потом снимаем PresentMon.
#
# Про вердикт (проверено на живой заявке): отношение презентов к кадрам движка доказывает только
# «FG работает» (если > 1.5). Обратное неверно — PresentMon по --process_id сгенерированные кадры
# не видит и при живом DLSS-G даёт ровно 1.00. Правду показал оверлей NVIDIA: «DLSS 160 | К/с 80».
# Ценность замера здесь — базовый fps и режим представления, а не вердикт.
#
#   szcli exec <СЗ> -f tools\recipes\client\fg-wait-and-measure.ps1

$ErrorActionPreference = 'SilentlyContinue'
$tool      = 'C:\Users\nekit\Desktop\client\tools\presentmon\PresentMon.exe'
$csv       = Join-Path $env:TEMP 'szdiag-presentmon.csv'
$sec       = 12
$waitMax   = 300
$gpuFloor  = 35

$proc = Get-Process Stalker2-Win64-Shipping
if (-not $proc) { '   игра не запущена'; return }

'=== жду устоявшегося рендера (GPU 3D > {0}%, максимум {1} с) ===' -f $gpuFloor, $waitMax
$sw = [Diagnostics.Stopwatch]::StartNew()
$hits = 0
while ($sw.Elapsed.TotalSeconds -lt $waitMax) {
    $gpu = ((Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage').CounterSamples |
            Where-Object { $_.InstanceName -match "pid_$($proc.Id)_" } |
            Measure-Object CookedValue -Sum).Sum
    if ($gpu -gt $gpuFloor) { $hits++ } else { $hits = 0 }
    '   {0,4:N0} с   GPU 3D {1,5:N1}%   подряд {2}' -f $sw.Elapsed.TotalSeconds, $gpu, $hits
    if ($hits -ge 3) { break }
    Start-Sleep -Seconds 5
}
if ($hits -lt 3) { '   рендер так и не устоялся — замер всё равно сниму, но смотри на цифры критически' }

'=== замер {0} с ===' -f $sec
Remove-Item $csv -Force
$argv = @('--process_id', $proc.Id, '--output_file', $csv, '--timed', $sec,
          '--terminate_after_timed', '--stop_existing_session')
$p = Start-Process $tool -ArgumentList $argv -PassThru -WindowStyle Hidden
$null = $p.WaitForExit(($sec + 25) * 1000)
if (-not (Test-Path $csv)) { '   CSV не создан'; return }

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

'   кадров движка:      {0,6:N1} fps' -f $appFps
'   презентов:          {0,6:N1} fps' -f $present
'   показано на экране: {0,6:N1} fps' -f $display
if ($appFps -and $present) {
    $k = $present / $appFps
    '   презентов на кадр движка: {0:N2}' -f $k
    if ($k -gt 1.5) { '   >>> ГЕНЕРАЦИЯ КАДРОВ РАБОТАЕТ' }
    else            { '   отношение ~1.00 — вердикта не даёт (замер не видит сгенерированные кадры),'
                      '   смотреть строку DLSS в оверлее NVIDIA' }
}
$rows | Group-Object PresentMode | Sort-Object Count -Descending |
    ForEach-Object { '   режим: {0,-34} {1:N0} кадров' -f $_.Name, $_.Count }

Remove-Item $csv -Force
