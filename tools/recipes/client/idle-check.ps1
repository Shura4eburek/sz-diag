$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Машина «стоит в простое и ждёт вырубона» — проверка, что она действительно СТОИТ:
# нет забытых стресс-тулов, нет запланированных задач, которые их поднимут, и есть чем
# датировать простой (последняя загрузка + последние Kernel-Power 41 / 6008).
# Без этого «простояла N часов» может оказаться «стояла под нагрузкой» или наоборот —
# «спала», а сон вырубоны не ловит.
#   szcli exec <СЗ> -f tools\recipes\client\idle-check.ps1

$os = Get-CimInstance Win32_OperatingSystem
$now = Get-Date
"Аптайм        : {0:N1} ч (загрузка {1:dd.MM HH:mm:ss})" -f ($now - $os.LastBootUpTime).TotalHours, $os.LastBootUpTime
"Загрузка CPU  : {0} %" -f (Get-CimInstance Win32_Processor).LoadPercentage

'Стресс-процессы: ' + $(
    $p = Get-Process | Where-Object { $_.Name -match 'OCCT|furmark|lhmmon|3dmark|TM5|GPU3D|CpuOcct|GpuMemtest|Prime95|y-cruncher' }
    if ($p) { ($p | ForEach-Object { "$($_.Name)(pid $($_.Id))" }) -join ', ' } else { 'нет — машина реально простаивает' }
)

'Задачи szdiag  :'
(schtasks /query /fo csv /nh) -split "`r?`n" | Where-Object { $_ -match 'szdiag' } | ForEach-Object {
    $f = $_ -split '","'
    "   {0,-32} {1,-20} {2}" -f $f[0].Trim('"'), $f[1].Trim('"'), $f[-1].Trim('"')
}

# Спящая машина вырубоны не ловит: простой должен быть бодрствующим
'Сон/гибернация :'
$pl = powercfg /getactivescheme
"   схема: $pl"
foreach ($k in @('STANDBYIDLE', 'HIBERNATEIDLE')) {
    $v = (powercfg /query SCHEME_CURRENT SUB_SLEEP $k 2>$null | Select-String 'Current AC Power Setting Index') -replace '.*:\s*', ''
    "   $k (AC): $v"
}

'Последние вырубоны (Kernel-Power 41 / EventLog 6008):'
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41, 6008 } -MaxEvents 5 -ErrorAction SilentlyContinue
if ($ev) { $ev | ForEach-Object { "   {0:dd.MM HH:mm:ss}  Id={1}" -f $_.TimeCreated, $_.Id } } else { '   нет в журнале' }
