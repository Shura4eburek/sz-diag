$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разведка «оптимизаторов» и твиков винды: кто и что покрутил под капотом.
# Грабля: 162003 — инпут-лаг в CS, клиент сидит на Process Lasso + чём-то ещё.
#   szcli exec <СЗ> -f tools\recipes\client\tweaks-recon.ps1

'== ОС'
$os = Get-CimInstance Win32_OperatingSystem
"   {0} build {1}  установлена {2:yyyy-MM-dd}" -f $os.Caption, $os.BuildNumber, $os.InstallDate
"   загрузка: {0:yyyy-MM-dd HH:mm}" -f $os.LastBootUpTime

'== установленный софт (не MS)'
$keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
$apps = foreach ($k in $keys) { Get-ItemProperty $k -ErrorAction SilentlyContinue }
$apps | Where-Object { $_.DisplayName } | Sort-Object DisplayName -Unique | ForEach-Object {
    $mark = ''
    if ($_.DisplayName -match 'Lasso|Optimi|Booster|Tuner|Cleaner|Tweak|Razer|CCleaner|Wise|Advanced System|Auslogics|IObit|MSI Center|Armoury|Afterburner|Rivatuner|Defender Control|Toolkit|Debloat|Atlas|Revision') { $mark = '  <== оптимизатор/тюнер' }
    "   {0,-60} {1}{2}" -f $_.DisplayName, $_.DisplayVersion, $mark
}

'== автозагрузка (Run)'
foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($p) { $p.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { "   {0} = {1}" -f $_.Name, $_.Value } }
}

'== bcdedit (таймеры/ядра — классика инпут-лага)'
(bcdedit /enum '{current}') -split "`r?`n" | Where-Object { $_ -match 'useplatform|dynamictick|tscsync|numproc|x2apic|nx|hypervisor|bootmenupolicy|increaseuserva' } | ForEach-Object { "   $($_.Trim())" }

'== реестр: планировщик, игры, ввод'
function RegGet($path, $name) {
    $v = (Get-ItemProperty $path -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -eq $v) { 'нет' } else { $v }
}
"   Win32PrioritySeparation = {0}   (дефолт 2 / 26 hex)" -f (RegGet 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation')
"   SystemResponsiveness    = {0}   (дефолт 20)" -f (RegGet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'SystemResponsiveness')
"   NetworkThrottlingIndex  = {0}   (дефолт 10)" -f (RegGet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex')
"   Games:GPU Priority      = {0} / Priority = {1} / SFIO = {2}" -f (RegGet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games' 'GPU Priority'), (RegGet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games' 'Priority'), (RegGet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games' 'Scheduling Category')
"   HAGS (HwSchMode)        = {0}   (2=вкл 1=выкл)" -f (RegGet 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode')
"   TdrDelay / TdrLevel     = {0} / {1}" -f (RegGet 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'TdrDelay'), (RegGet 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'TdrLevel')
"   GameDVR AppCaptureEnabled = {0} / GameDVR_Enabled = {1}" -f (RegGet 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled'), (RegGet 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled')
"   Мышь: Speed={0} Th1={1} Th2={2}  (accel: 1/6/10 = вкл, 0/0/0 = выкл)" -f (RegGet 'HKCU:\Control Panel\Mouse' 'MouseSpeed'), (RegGet 'HKCU:\Control Panel\Mouse' 'MouseThreshold1'), (RegGet 'HKCU:\Control Panel\Mouse' 'MouseThreshold2')
"   PowerThrottlingOff      = {0}" -f (RegGet 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' 'PowerThrottlingOff')

'== схема питания'
$scheme = (powercfg /getactivescheme)
"   $scheme"
$g = [regex]::Match($scheme, '([0-9a-f]{8}-[0-9a-f-]+)').Value
(powercfg /query $g SUB_PROCESSOR) -split "`r?`n" | Where-Object { $_ -match 'GUID псевдонима|GUID Alias|Индекс параметра|Current AC Power Setting|Псевдоним' } | Select-Object -First 60 | ForEach-Object { "   $($_.Trim())" }

'== Process Lasso'
$pl = Get-Process -Name 'ProcessLasso','ProcessGovernor' -ErrorAction SilentlyContinue
if ($pl) { $pl | ForEach-Object { "   процесс {0} pid={1}" -f $_.Name, $_.Id } } else { '   процессы Process Lasso не найдены' }
foreach ($cfg in @("$env:ProgramData\ProcessLasso\prolasso.ini", "$env:ProgramFiles\Process Lasso\prolasso.ini", "${env:ProgramFiles(x86)}\Process Lasso\prolasso.ini")) {
    if (Test-Path $cfg) {
        "   конфиг: $cfg"
        Get-Content $cfg | Where-Object { $_ -match '\S' -and $_ -notmatch '^;' } | ForEach-Object { "      $_" }
    }
}

'== службы, выключенные вручную (из штатных)'
$watch = 'SysMain','wuauserv','UsoSvc','WaaSMedicSvc','BITS','Spooler','WSearch','Themes','AudioSrv','Audiosrv','DPS','DiagTrack','NvContainerLocalSystem','WinDefend','SecurityHealthService','Power','LanmanWorkstation'
Get-Service -ErrorAction SilentlyContinue | Where-Object { $watch -contains $_.Name } | ForEach-Object {
    $st = (Get-CimInstance Win32_Service -Filter "Name='$($_.Name)'").StartMode
    "   {0,-26} {1,-9} StartMode={2}" -f $_.Name, $_.Status, $st
}

'== защита'
Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { "   AV: $($_.displayName)" }

'== GPU / драйвер'
Get-CimInstance Win32_VideoController | ForEach-Object { "   {0}  drv {1}  ({2:yyyy-MM-dd})" -f $_.Name, $_.DriverVersion, $_.DriverDate }
