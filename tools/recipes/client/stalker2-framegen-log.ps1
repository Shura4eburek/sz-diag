$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Прямая причина отказа DLSS Frame Generation — из лога самой игры (СЗ 123456).
#
# Грабля: конфиг игры показывает только «что просили» (Method=DLSSG), а движок/Streamline
# молча откатывается и пишет причину в Stalker2.log (LogDLSS/LogStreamline/LogNGX). Плюс
# точное состояние HAGS: ключ HwSchMode может ОТСУТСТВОВАТЬ — это тоже «выключен», но чтение
# одного значения через Get-ItemProperty -Name это не отличает от ошибки доступа.
#
#   szcli exec <СЗ> -f tools\recipes\client\stalker2-framegen-log.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== HAGS: все значения ключа GraphicsDrivers ==='
$gd = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
$p = Get-ItemProperty $gd
$p.PSObject.Properties | Where-Object { $_.Name -match 'HwSch|Tdr|MPO|Dxgk' } |
    ForEach-Object { '   {0,-24} = {1}' -f $_.Name, $_.Value }
'   -- на месте ли ключ вообще: {0}' -f (Test-Path $gd)

'=== Конфиги ReX целиком (FrameGeneration / LowLatency / SuperResolution) ==='
foreach ($f in (Get-ChildItem 'C:\Users\*\AppData\Local\Stalker2\Saved\Config\Windows\GSCReX*.ini')) {
    "   -- $($f.Name)"
    Get-Content $f.FullName | ForEach-Object { '      ' + $_ }
}

'=== Логи игры: последние запуски ==='
$logs = Get-ChildItem 'C:\Users\*\AppData\Local\Stalker2\Saved\Logs\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 4
foreach ($l in $logs) { '   {0,-34} {1:dd.MM.yyyy HH:mm}  {2:N0} б' -f $l.Name, $l.LastWriteTime, $l.Length }

$main = $logs | Select-Object -First 1
if ($main) {
    "=== $($main.FullName): строки про GPU / DLSS / FrameGen / Streamline ==="
    Get-Content $main.FullName |
        Select-String -Pattern 'DLSS|Streamline|NGX|FrameGen|ReX|Reflex|NVAPI|D3D12.*Adapter|GPU Adapter|Driver Version|Feature Level|not supported|unsupported|fail|denied' |
        Select-Object -First 120 |
        ForEach-Object { '   ' + $_.Line.Trim() }
}
