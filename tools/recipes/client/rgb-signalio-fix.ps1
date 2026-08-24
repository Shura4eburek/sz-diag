# СЗ 163013: SignalRGB не стартует — "SignalIO driver outdated: installed=0.0.0, need=1.3.7",
# апп вечно висит в состоянии InstallationRequired.
#
# ГРАБЛЯ (корень): служба ядра SignalRgbDriver уже существовала, но была ОТКЛЮЧЕНА (Start=4).
# Элевейтед-установщик самого SignalRGB (--installsignalio) в такой ситуации бессилен:
#   SignalIO: Failed to start Driver Service! Error Code: 1058   (служба отключена)
#   SignalIO: Created Driver file!
#   SignalIO: Failed to Create Service! Error Code: 1073         (ERROR_SERVICE_EXISTS)
# То есть .sys он перезаписывает, а конфиг существующей службы починить не умеет — замкнутый круг.
# Лечение: вернуть Start=3 вручную, поднять драйвер и перезапустить апп ЭЛЕВЕЙТЕД в сессии
# пользователя (без админа SignalRgb.exe получает Error 5 на OpenSCManager и снова не откроет драйвер).
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\SignalRgbDriver'

Write-Output "=== ДИАГНОСТИКА ==="
if (-not (Test-Path $k)) { Write-Output "службы SignalRgbDriver нет — SignalRGB просто не устанавливал драйвер"; }
else { "Start = $((Get-ItemProperty $k).Start)  (4 = DISABLED, это и есть болезнь)" }
(sc.exe query SignalRgbDriver | Select-String 'STATE').Line
$drv = "$env:SystemRoot\System32\Drivers\SignalRgbDriver.sys"
if (Test-Path $drv) {
  $sig = Get-AuthenticodeSignature $drv
  "подпись: $($sig.Status) / $($sig.SignerCertificate.Subject -replace ',.*','')"
}

Write-Output "=== ЛЕЧЕНИЕ: включаем службу драйвера ==="
sc.exe config SignalRgbDriver start= demand
sc.exe start SignalRgbDriver
Start-Sleep -Seconds 2
(sc.exe query SignalRgbDriver | Select-String 'STATE').Line

Write-Output "=== ПЕРЕЗАПУСК АППА (elevated, в сессии пользователя) ==="
$user = (Get-CimInstance Win32_ComputerSystem).UserName    # DOMAIN\user активной сессии
if (-not $user) { Write-Output "активной сессии нет — апп поднимется сам при следующем входе"; return }
Get-Process SignalRgb,SignalRgbLauncher,SignalRgbService -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Set-Service SignalRgb.Service -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service SignalRgb.Service -ErrorAction SilentlyContinue

$exe = Get-ChildItem 'C:\Users\*\AppData\Local\VortxEngine\app-*\SignalRgbLauncher.exe' -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$task = 'szdiag-signalrgb-start'
schtasks /delete /tn $task /f 2>$null | Out-Null
# /it + /rl highest — иначе GUI не появится (сессия 0) или уйдёт без прав и снова упрётся в Error 5
schtasks /create /tn $task /tr "`"$exe`"" /sc once /st 23:59 /ru $user /rl highest /it /f | Out-Null
schtasks /run /tn $task | Out-Null
Start-Sleep -Seconds 25
schtasks /delete /tn $task /f | Out-Null

Write-Output "=== ПРОВЕРКА: что в свежем логе ==="
$log = Get-ChildItem 'C:\Users\*\AppData\Local\WhirlwindFX\SignalRgb\Logs\*.log' -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
  Get-Content $log.FullName | Where-Object { $_ -match 'InstallationRequired|SignalIO|STATE ENTERED' } |
    Select-Object -Last 15
}
