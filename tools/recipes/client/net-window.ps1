[Console]::OutputEncoding = [Text.Encoding]::UTF8
# СЕТЕВАЯ КАРТИНА ЗА КОНКРЕТНОЕ ОКНО ДАТ + питание и диски в том же окне.
# Отвечает на вопрос «где физически стояла машина и что с ней делали в такой-то день».
#
# Грабля (СЗ 161346): спор «кто переставил накопитель 29.07 между 12:11 и 13:57» — клиент
# говорит «ПК был у вас», сервис не помнит. Полный журнал NetworkProfile за месяц не влезает
# в вывод (тысячи строк переподключений), нужен срез по окну — и рядом события старта/сна,
# чтобы видеть, работала ли машина в спорные часы вообще.
#
# Правь $From/$To под нужное окно.
# Использование: bash tools/recipes/host/ssh-run.sh tools/recipes/client/net-window.ps1 <IP>

$From = Get-Date '2026-07-27 00:00'
$To   = Get-Date '2026-07-30 23:59'

"=== Окно: {0:dd.MM.yyyy HH:mm} — {1:dd.MM.yyyy HH:mm} ===" -f $From, $To

''
'--- Сеть: к какой сети подключалась (NetworkProfile 10000/10001) ---'
# Схлопываем серии переподключений к одной и той же сети: интересен факт и границы, не шум
$ev = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-NetworkProfile/Operational'
        Id        = 10000, 10001
        StartTime = $From
        EndTime   = $To
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
if (-not $ev) { '  событий нет' }
$prev = $null
foreach ($e in $ev) {
    $x = [xml]$e.ToXml(); $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $name = $d['Name']
    if ($name -match 'Триває|Identifying') { continue }
    $act = if ($e.Id -eq 10000) { '+' } else { '-' }
    $key = "$act$name"
    if ($key -ne $prev) {
        "  {0:dd.MM HH:mm:ss}  {1} {2}" -f $e.TimeCreated, $act, $name
        $prev = $key
    }
}

''
'--- Питание: старты, выключения, сон (Kernel-General 12/13, Kernel-Power 41/42/107/109) ---'
$pw = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        Id        = 12, 13, 41, 42, 107, 109, 1074
        StartTime = $From
        EndTime   = $To
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
foreach ($e in $pw) {
    $what = switch ($e.Id) {
        12    { 'СТАРТ ОС' }
        13    { 'штатное завершение' }
        41    { '!!! ВЫРУБОН (Kernel-Power 41)' }
        42    { 'уход в сон' }
        107   { 'выход из сна' }
        109   { 'ядро инициировало выключение' }
        1074  { 'выключение/перезагрузка по команде' }
        default { "Id=$($e.Id)" }
    }
    $extra = ''
    if ($e.Id -eq 1074) {
        $m = ($e.Message -replace '\s+', ' ').Trim()
        if ($m.Length -gt 100) { $m = $m.Substring(0, 100) }
        $extra = " — $m"
    }
    "  {0:dd.MM HH:mm:ss}  {1}{2}" -f $e.TimeCreated, $what, $extra
}

''
'--- Диски: подключения накопителей (Partition/Diagnostic 1006) ---'
# Несёт DiskNumber, серийник, Adapter (RaidPortN) и Bus — по нему видно, в каком слоте
# стоял накопитель на момент события, то есть и сам факт перестановки
$pd = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-Partition/Diagnostic'
        Id        = 1006
        StartTime = $From
        EndTime   = $To
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
if (-not $pd) { '  событий нет' }
foreach ($e in $pd) {
    $x = [xml]$e.ToXml(); $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $model = ($d['Model'] -replace '\s+', ' ').Trim()
    if (-not $model) { continue }
    "  {0:dd.MM HH:mm:ss}  Disk{1}  {2}  SN={3}  Adapter={4}  Bus={5}" -f `
        $e.TimeCreated, $d['DiskNumber'], $model, $d['SerialNumber'], $d['Adapter'], $d['Bus']
}

''
'--- Ошибки накопителей и WHEA в том же окне ---'
$err = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        ProviderName = @('disk', 'stornvme', 'storahci', 'Ntfs', 'volmgr', 'Microsoft-Windows-WHEA-Logger')
        StartTime    = $From
        EndTime      = $To
    } -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -notin 'Сведения', 'Information', 'Відомості' } | Sort-Object TimeCreated)
if (-not $err) { '  событий нет' }
foreach ($e in $err) {
    $m = ($e.Message -replace '\s+', ' ').Trim()
    if ($m.Length -gt 110) { $m = $m.Substring(0, 110) }
    "  {0:dd.MM HH:mm:ss}  {1} Id={2} — {3}" -f $e.TimeCreated, $e.ProviderName, $e.Id, $m
}
