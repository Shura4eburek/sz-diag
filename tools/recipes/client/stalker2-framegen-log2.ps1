$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Добивка по FG (СЗ 123456): HAGS в ветке класса дисплея, RHI/адаптер из лога игры, хвост лога,
# крэши и полный конфиг GameUserSettings.
#
# Грабля: HwSchMode может жить не только в Control\GraphicsDrivers — состояние поддержки пишет
# драйвер в ветку класса адаптера {4d36e968-...}\000N. А в Stalker2.log первые 56 КБ — это ещё
# инициализация; строки про RHI/адаптер и реальный отказ DLSS-G идут дальше, поэтому смотрим
# и по секциям, и хвост.
#
#   szcli exec <СЗ> -f tools\recipes\client\stalker2-framegen-log2.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== HAGS в ветке класса дисплея ==='
$cls = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
foreach ($k in (Get-ChildItem $cls | Where-Object PSChildName -match '^\d{4}$')) {
    $pp = Get-ItemProperty $k.PSPath
    if (-not $pp.DriverDesc) { continue }
    "   -- $($k.PSChildName): $($pp.DriverDesc)"
    $pp.PSObject.Properties | Where-Object { $_.Name -match 'HwSch|Preempt|GPUScheduler|MPO' } |
        ForEach-Object { '      {0,-34} = {1}' -f $_.Name, $_.Value }
}

'=== GameUserSettings.ini целиком (Steam-профиль) ==='
$gus = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\GameUserSettings.ini'
Get-Content $gus | ForEach-Object { '   ' + $_ }

'=== Stalker2.log: RHI/адаптер/разрешение ==='
$log = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Logs\Stalker2.log'
Get-Content $log | Select-String -Pattern 'LogRHI|LogD3D12RHI|LogWindows: .*Adapter|Adapter Name|VendorId|DeviceId|Driver Version|LogInit: .*Version|CL-|Branch' |
    Select-Object -First 40 | ForEach-Object { '   ' + $_.Line.Trim() }

'=== Stalker2.log: последние 60 строк (чем закончился запуск) ==='
Get-Content $log -Tail 60 | ForEach-Object { '   ' + $_ }

'=== Крэши сегодня ==='
Get-ChildItem 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Crashes' -Directory |
    Sort-Object LastWriteTime -Descending | Select-Object -First 5 |
    ForEach-Object { '   {0}   {1:dd.MM.yyyy HH:mm}' -f $_.Name, $_.LastWriteTime }
