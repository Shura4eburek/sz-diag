$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Инпут-лаг в игре: сначала проверяем тупое — куда воткнут монитор, на какой частоте
# он реально работает, и что накрутил Process Lasso (ProBalance/affinity/приоритеты).
# Грабля: 162003 — жалоба «инпут-лаг в CS», монитор 240 Гц + iGPU в системе.
#   szcli exec <СЗ> -f tools\recipes\client\display-and-lasso.ps1

'== видеоадаптеры и активный режим'
Get-CimInstance Win32_VideoController | ForEach-Object {
    "   {0}" -f $_.Name
    "      режим: {0}x{1} @ {2} Гц   статус={3}  ошибка={4}" -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate, $_.Status, $_.ConfigManagerErrorCode
    "      мин/макс частота: {0}/{1}" -f $_.MinRefreshRate, $_.MaxRefreshRate
}

'== мониторы (EDID)'
$ids = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue
$conn = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction SilentlyContinue
$tech = @{ 0='VGA/D-Sub'; 1='S-Video'; 2='Composite'; 3='Component'; 4='DVI'; 5='HDMI'; 6='LVDS'; 8='D-Jpn'; 9='SDI'; 10='DisplayPort (внешний)'; 11='DisplayPort (встроенный)'; 12='UDI внешний'; 13='UDI встроенный'; 14='SDTV'; 15='Miracast'; 2147483648='Internal' }
foreach ($m in $ids) {
    $name = -join ($m.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $sn = -join ($m.SerialNumberID | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $c = $conn | Where-Object { $_.InstanceName -eq $m.InstanceName }
    $t = if ($c) { $tech[[int]$c.VideoOutputTechnology] } else { '?' }
    if (-not $t) { $t = "код $($c.VideoOutputTechnology)" }
    "   {0}  SN={1}  год={2}  подключение: {3}" -f $name, $sn, $m.YearOfManufacture, $t
    "      instance: $($m.InstanceName)"
}

'== все режимы, что видит система для основного дисплея'
Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
try {
    $s = [System.Windows.Forms.Screen]::AllScreens
    $s | ForEach-Object { "   {0} primary={1} bounds={2}" -f $_.DeviceName, $_.Primary, $_.Bounds }
} catch { "   (не удалось: $($_.Exception.Message))" }

'== что рисует рабочий стол (какой адаптер)'
Get-CimInstance Win32_DesktopMonitor -ErrorAction SilentlyContinue | ForEach-Object { "   {0}  {1}x{2}" -f $_.Name, $_.ScreenWidth, $_.ScreenHeight }

'== Process Lasso: файлы конфигурации'
$roots = @("$env:ProgramData\ProcessLasso", "$env:LOCALAPPDATA\ProcessLasso", "$env:APPDATA\ProcessLasso", "$env:ProgramFiles\Process Lasso", "${env:ProgramFiles(x86)}\Process Lasso")
foreach ($r in $roots) {
    if (Test-Path $r) {
        Get-ChildItem $r -Recurse -Include *.ini,*.cfg,*.pl_config,*.dat -ErrorAction SilentlyContinue | ForEach-Object {
            "   {0}  {1} байт  {2:yyyy-MM-dd HH:mm}" -f $_.FullName, $_.Length, $_.LastWriteTime
        }
    }
}
'== Process Lasso: содержимое prolasso.ini (правила)'
$ini = Get-ChildItem @($roots | Where-Object { Test-Path $_ }) -Recurse -Filter 'prolasso.ini' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ini) {
    "   файл: $($ini.FullName)"
    Get-Content $ini.FullName -Encoding UTF8 | Where-Object { $_ -match '\S' } | ForEach-Object { "      $_" }
} else { '   prolasso.ini не найден' }

'== Process Lasso: где живёт exe и автозапуск'
Get-CimInstance Win32_Process -Filter "Name='ProcessLasso.exe' OR Name='ProcessGovernor.exe'" | ForEach-Object {
    "   {0}  pid={1}  {2}" -f $_.Name, $_.ProcessId, $_.ExecutablePath
    "      cmd: $($_.CommandLine)"
}
(schtasks /query /fo csv /nh) -split "`r?`n" | Where-Object { $_ -match -join('Lasso') } | ForEach-Object { "   задача: $_" }

'== игры: что стоит и где'
foreach ($p in @('C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive')) {
    if (Test-Path $p) { "   CS2: $p" }
}
Get-ChildItem 'C:\Program Files (x86)\Steam\steamapps' -Filter 'appmanifest_*.acf' -ErrorAction SilentlyContinue | ForEach-Object {
    $n = (Select-String -Path $_.FullName -Pattern '"name"\s+"(.+)"').Matches.Groups[1].Value
    "   steam app: $n"
}

'== CS2: cfg с настройками (autoexec/video)'
$cs = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg'
if (Test-Path $cs) {
    Get-ChildItem $cs -Filter '*.cfg' | ForEach-Object { "   {0}  {1:yyyy-MM-dd HH:mm}" -f $_.Name, $_.LastWriteTime }
}
$vs = Get-ChildItem 'C:\Program Files (x86)\Steam\userdata' -Recurse -Filter 'cs2_video.txt' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($vs) {
    "   видео-настройки CS2: $($vs.FullName)"
    Get-Content $vs.FullName | ForEach-Object { "      $_" }
}
$vd = Get-ChildItem 'C:\Program Files (x86)\Steam\userdata' -Recurse -Filter 'localconfig.vdf' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($vd) {
    $lo = Select-String -Path $vd.FullName -Pattern 'LaunchOptions' -Context 0,1
    if ($lo) { $lo | ForEach-Object { "   launch options: $($_.Line.Trim())" } }
}
