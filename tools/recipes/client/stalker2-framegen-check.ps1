$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Почему NVIDIA Frame Generation не включается в игре (СЗ 123456, S.T.A.L.K.E.R. 2).
#
# Грабля: в настройках игры переключатель стоит «включено», а кадры не генерируются — движок
# молча откатывается, если не выполнено внешнее условие. Порядок причин по частоте:
#   1) HAGS (Hardware-accelerated GPU scheduling) выключен — DLSS FG без него не стартует ВООБЩЕ;
#   2) в системе несколько адаптеров и игра рендерится не на RTX;
#   3) nvngx_dlssg.dll отсутствует/старый в папке игры;
#   4) V-Sync / внешний лимитер FPS (RTSS, панель NVIDIA Max Frame Rate) душит FG;
#   5) Reflex выключен (FG требует Reflex on).
# Скрипт только СОБИРАЕТ факты, ничего не меняет.
#
#   szcli exec <СЗ> -f tools\recipes\client\stalker2-framegen-check.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== 1. HAGS (главный подозреваемый) ==='
$gd = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
$mode  = (Get-ItemProperty $gd -Name HwSchMode).HwSchMode
$state = (Get-ItemProperty $gd -Name HwSchState).HwSchState
$sup   = (Get-ItemProperty $gd -Name HwSchSupported).HwSchSupported
$modeTxt = switch ($mode) { 2 {'2 = ВКЛЮЧЕН'} 1 {'1 = ВЫКЛЮЧЕН'} default {"$mode (не задан)"} }
"   HwSchMode      : $modeTxt"
"   HwSchState     : $state   (0 выкл / 1 вкл по умолч. / 2 вкл пользователем)"
"   HwSchSupported : $sup"
if ($mode -ne 2) { '   >>> HAGS ВЫКЛЮЧЕН — DLSS Frame Generation работать не будет, переключатель в игре бесполезен.' }

'=== 2. Адаптеры: на чём реально рендерит ==='
Get-CimInstance Win32_VideoController | ForEach-Object {
    '   {0,-34} драйвер {1,-14} {2}  статус {3}' -f $_.Name, $_.DriverVersion, $_.DriverDate, $_.Status
}
'   -- предпочтение GPU для приложений (Windows Graphics Settings)'
$pref = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences'
if ($pref) {
    $pref.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } |
        ForEach-Object { '   {0} = {1}' -f $_.Name, $_.Value }
} else { '   (не задано)' }

'=== 3. Файлы игры и DLSS-библиотеки ==='
$roots = @()
$roots += Get-ChildItem 'C:\Program Files (x86)\Steam\steamapps\common' -Directory | Where-Object Name -match 'S.T.A.L.K.E.R|Stalker'
foreach ($lib in (Get-ChildItem 'C:\','D:\','E:\','F:\' -Directory -Filter 'SteamLibrary')) {
    $roots += Get-ChildItem (Join-Path $lib.FullName 'steamapps\common') -Directory | Where-Object Name -match 'S.T.A.L.K.E.R|Stalker'
}
$roots += Get-ChildItem 'C:\','D:\','E:\','F:\' -Directory | Where-Object Name -match '^S\.T\.A\.L\.K\.E\.R|^Stalker'
$roots += Get-ChildItem 'C:\Program Files\','C:\Program Files\Epic Games\','D:\Games','C:\Games','D:\Epic Games' -Directory | Where-Object Name -match 'S.T.A.L.K.E.R|Stalker'
$roots = $roots | Sort-Object FullName -Unique
if (-not $roots) { '   папка игры не найдена в типовых местах' }
foreach ($r in $roots) {
    "   -- $($r.FullName)"
    $exe = Get-ChildItem $r.FullName -Recurse -Filter 'Stalker2*.exe' -Depth 4 | Select-Object -First 3
    foreach ($e in $exe) {
        '      exe {0}  {1}  {2:dd.MM.yyyy}' -f $e.Name, $e.VersionInfo.FileVersion, $e.LastWriteTime
    }
    $dlls = Get-ChildItem $r.FullName -Recurse -Include 'nvngx_dlssg.dll','nvngx_dlss.dll','nvngx_dlssd.dll','sl.*.dll','nvapi64.dll' -Depth 6
    if (-not $dlls) { '      DLSS/Streamline DLL не найдены (!)' }
    foreach ($d in $dlls) {
        '      {0,-26} {1,-16} {2:dd.MM.yyyy}  {3:N0} б' -f $d.Name, $d.VersionInfo.FileVersion, $d.LastWriteTime, $d.Length
    }
}

'=== 4. Конфиг игры (что реально записано) ==='
$cfgDirs = Get-ChildItem 'C:\Users\*\AppData\Local\Stalker2\Saved\Config' -Directory -Recurse -Depth 1
foreach ($c in $cfgDirs) {
    foreach ($ini in (Get-ChildItem $c.FullName -Filter '*.ini')) {
        "   -- $($ini.FullName)  (изменён $($ini.LastWriteTime.ToString('dd.MM.yyyy HH:mm')))"
        Get-Content $ini.FullName |
            Select-String -Pattern 'FrameGeneration|Generation|DLSS|FSR|Reflex|VSync|FrameRateLimit|MaxFPS|Fullscreen|WindowMode|ScreenPercentage|Upscal' |
            ForEach-Object { '      ' + $_.Line.Trim() }
    }
}

'=== 5. Внешние лимитеры/оверлеи, которые ломают FG ==='
Get-Process | Where-Object { $_.ProcessName -match 'RTSS|RivaTuner|Afterburner|EVGA|Precision|Discord|obs|GeForce|nvcontainer|Overwolf|Razer|MSIAfterburner|Xbox' } |
    ForEach-Object { '   {0,-24} pid {1,-7} старт {2:dd.MM HH:mm}' -f $_.ProcessName, $_.Id, $_.StartTime }
'   -- Xbox Game Bar / Game DVR'
$gb = Get-ItemProperty 'HKCU:\System\GameConfigStore'
'   GameDVR_Enabled = {0}   GameDVR_FSEBehavior = {1}   HonorUserFSEBehaviorMode = {2}' -f $gb.GameDVR_Enabled, $gb.GameDVR_FSEBehavior, $gb.GameDVR_HonorUserFSEBehaviorMode
'   AllowGameDVR (policy) = {0}' -f (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR').AllowGameDVR

'=== 6. Дисплеи и частота ==='
Get-CimInstance -Namespace root\wmi WmiMonitorBasicDisplayParams | ForEach-Object { '   монитор: {0}' -f $_.InstanceName }
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object { '   {0}  {1}x{2}  primary={3}' -f $_.DeviceName, $_.Bounds.Width, $_.Bounds.Height, $_.Primary }
Get-CimInstance Win32_VideoController | ForEach-Object { '   текущий режим: {0}x{1} @ {2} Гц' -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate }

'=== 7. ОС и база профилей драйвера ==='
$os = Get-CimInstance Win32_OperatingSystem
'   {0}  build {1}' -f $os.Caption, $os.BuildNumber
Get-ChildItem (Join-Path $env:ProgramData 'NVIDIA Corporation\Drs') |
    ForEach-Object { '   {0,-16} изменён {1:dd.MM.yyyy HH:mm}' -f $_.Name, $_.LastWriteTime }
