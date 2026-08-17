[Console]::OutputEncoding = [Text.Encoding]::UTF8
# СНИМОК СРАЗУ ПОСЛЕ ОТКАЗА: всё, что теряется или размывается, пока машина живая.
#
# Грабля (СЗ 161346, 17.08): дефект наконец воспроизвёлся живьём — hard-off, после которого
# накопитель не поднимается до полного обесточивания. Машина в таком состоянии живёт минутами,
# и в эти минуты нужно снять сразу всё: SMART целевого диска (счётчик ошибок контроллера —
# главный маркер отвала), последние KP41 с bugcheck, раскладку слотов и свежие дампы. Ходить
# за этим по очереди — значит потерять половину к следующему отказу.
#
# Использование: bash tools/recipes/host/ssh-run.sh tools/recipes/client/crash-snapshot.ps1 <IP>

"=== СНИМОК {0:dd.MM.yyyy HH:mm:ss} ===" -f (Get-Date)
"Аптайм: {0}" -f ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime)
''
'--- Раскладка слотов (Partition/Diagnostic 1006, сегодня) ---'
$pd = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Partition/Diagnostic'; Id=1006; StartTime=(Get-Date).Date} -ErrorAction SilentlyContinue | Sort-Object TimeCreated
foreach ($e in $pd) {
    $x=[xml]$e.ToXml(); $d=@{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $m = ($d['Model'] -replace '\s+',' ').Trim()
    if ($m) { "  {0:HH:mm:ss}  Disk{1}  {2}  Adapter={3}  Bus={4}" -f $e.TimeCreated, $d['DiskNumber'], $m, $d['Adapter'], $d['Bus'] }
}
''
'--- SMART NVMe (health log page 02h) ---'
# Счётчики контроллера: ErrorLogEntries растёт на каждой ошибочной команде — прямой след отвала
foreach ($dev in (Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe')) {
    "  {0}" -f $dev.FriendlyName
    $ns = Get-StorageReliabilityCounter -PhysicalDisk $dev -ErrorAction SilentlyContinue
    if ($ns) {
        "     Температура {0} °C  ЧасыВключения {1}  Циклы {2}  ОшибокЧтения {3}  ОшибокЗаписи {4}  Износ {5}%" -f `
            $ns.Temperature, $ns.PowerOnHours, $ns.StartStopCycle, $ns.ReadErrorsTotal, $ns.WriteErrorsTotal, $ns.Wear
    }
}
''
'--- Kernel-Power 41 за сегодня ---'
$kp = Get-WinEvent -FilterHashtable @{LogName='System'; Id=41; StartTime=(Get-Date).Date} -ErrorAction SilentlyContinue | Sort-Object TimeCreated
if (-not $kp) { '  нет' }
foreach ($e in $kp) {
    $x=[xml]$e.ToXml(); $d=@{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    "  {0:HH:mm:ss}  bugcheck={1} btn={2} sleep={3}" -f $e.TimeCreated, $d['BugcheckCode'], $d['PowerButtonTimestamp'], $d['SleepInProgress']
}
''
'--- Ошибки накопителей и WHEA за сегодня ---'
$err = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName=@('disk','stornvme','storahci','Ntfs','volmgr','Microsoft-Windows-WHEA-Logger'); StartTime=(Get-Date).Date} -ErrorAction SilentlyContinue | Where-Object { $_.LevelDisplayName -notin 'Сведения','Information','Відомості' } | Sort-Object TimeCreated
if (-not $err) { '  нет' }
foreach ($e in $err) {
    $m = ($e.Message -replace '\s+',' ').Trim()
    if ($m.Length -gt 100) { $m = $m.Substring(0,100) }
    "  {0:HH:mm:ss}  {1} Id={2} — {3}" -f $e.TimeCreated, $e.ProviderName, $e.Id, $m
}
''
'--- Свежие минидампы ---'
$dmp = Get-ChildItem 'C:\Windows\Minidump' -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-1) }
if (-not $dmp) { '  нет за сутки' }
foreach ($f in $dmp) { "  {0}  {1:dd.MM HH:mm:ss}  {2:N0} КБ" -f $f.Name, $f.LastWriteTime, ($f.Length/1KB) }
