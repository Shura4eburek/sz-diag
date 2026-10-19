# Догасить службы Windows Update после `szcli freeze`.
#
# Грабля (164266, 16.09.2026): сразу после загрузки freeze выставляет Start=4 в реестре,
# но службы УЖЕ запущены — и заявка остаётся незащищённой до ребута. `freeze --status`
# честно это показывает («служба ЗАПУЩЕНА»), а чинить приходится руками.
#
# wuauserv/UsoSvc гасятся обычным Stop-Service; если держатся — глушим по PID
# svchost-группы (у службы своя группа wuauserv/netsvcs, но остановка через SCM
# надёжнее, поэтому PID — последний довод).
$ErrorActionPreference = 'Continue'

foreach ($name in @('wuauserv', 'UsoSvc', 'WaaSMedicSvc')) {
    $svc = Get-Service $name -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Output "$name : нет такой службы"; continue }
    if ($svc.Status -eq 'Stopped') { Write-Output "$name : уже остановлена"; continue }
    try {
        Stop-Service $name -Force -ErrorAction Stop
        Write-Output "$name : остановлена"
    } catch {
        Write-Output ("$name : Stop-Service не смог — " + $_.Exception.Message)
        # sc.exe stop иногда проходит там, где Stop-Service упирается в зависимости.
        $out = & sc.exe stop $name 2>&1
        Write-Output ("  sc stop: " + (($out | Out-String).Trim() -replace '\s+', ' '))
    }
}

Start-Sleep -Seconds 3
Write-Output '--- итог ---'
Get-Service wuauserv, UsoSvc, WaaSMedicSvc -ErrorAction SilentlyContinue |
    Select-Object Name, Status, StartType | Format-Table -AutoSize | Out-String -Width 80
