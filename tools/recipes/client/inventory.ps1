$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Инвентарь клиента перед прогоном: где живёт агент, какие тулы уже лежат, что осталось
# от прошлых сессий. Гонять первым — экономит цикл «запушил то, что уже есть».
#   szcli exec <СЗ> -f tools\recipes\client\inventory.ps1

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'SzDiag.Agent.exe не найден — агент запущен под другим именем?'; return }
$base = Split-Path $proc.ExecutablePath -Parent
"База агента: $base"
if ($base -match 'OneDrive') { '⚠ папка агента внутри OneDrive — тулы будут синхронизироваться в облако (бэклог п.63)' }

'== содержимое'
Get-ChildItem $base | ForEach-Object { "   {0} {1}" -f $(if ($_.PSIsContainer) { 'd' } else { 'f' }), $_.Name }

'== tools\'
$t = Join-Path $base 'tools'
if (Test-Path $t) {
    Get-ChildItem $t -Directory | ForEach-Object {
        $sz = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
        "   {0,-12} {1,8:N1} МБ" -f $_.Name, $sz
    }
} else { '   нет' }

'== задачи szdiag (хвосты прошлых прогонов видно здесь)'
(schtasks /query /fo csv /nh) -split "`r?`n" | Where-Object { $_ -match 'szdiag' } | ForEach-Object {
    $p = $_ -split '","'
    "   {0,-32} {1}" -f $p[0].Trim('"'), $p[-1].Trim('"')
}

'== процессы стресс-тулов'
$p = Get-Process | Where-Object { $_.Name -match 'OCCT|furmark|lhmmon|3dmark|TM5' }
if ($p) { $p | ForEach-Object { "   {0} pid={1} CPU={2:N0} c" -f $_.Name, $_.Id, $_.CPU } } else { '   нет' }

'== железо и диск'
Get-CimInstance Win32_VideoController | ForEach-Object { "   GPU: $($_.Name) drv=$($_.DriverVersion)" }
Get-CimInstance Win32_Processor | ForEach-Object { "   CPU: $($_.Name)" }
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    "   {0} свободно {1:N1} ГБ" -f $_.DeviceID, ($_.FreeSpace / 1GB)
}

'== защита (влияет на приборный захват)'
$mp = Get-MpComputerStatus -ErrorAction SilentlyContinue
if ($mp) { "   Defender realtime: $($mp.RealTimeProtectionEnabled)" }
$dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
if ($dg) { "   HVCI running: $($dg.SecurityServicesRunning -join ',') (2 = HVCI, режет kernel-драйвер lhmmon)" }
