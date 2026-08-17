[Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЧЬЯ ЭТО БЫЛА СЕТЬ: MAC шлюза для каждого сетевого профиля + журнал подключений к сетям.
#
# Грабля (СЗ 161346): по именам профилей («Мережа», «Мережа 2», «Мережа 3») сделали вывод,
# в чьей сети стояла машина в день спорной перестановки дисков — и ошиблись. Имя профиля
# не говорит НИЧЕГО: оно приезжает внутри образа Acronis вместе с системой, а «наша сеть»
# в сервисном боксе и «наша сеть» в цеху/шоуруме — это два разных шлюза и два разных профиля.
# Единственный твёрдый идентификатор — DefaultGatewayMac из Signatures: его сверяют с MAC
# роутера конкретной точки (`arp -a` на любой машине той сети).
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\net-signatures.ps1 --timeout 180
#                bash tools/recipes/host/ssh-run.sh tools/recipes/client/net-signatures.ps1 <IP>

function ToDate($b) {
    if (-not $b -or $b.Count -lt 16) { return '' }
    $u = for ($i = 0; $i -lt 16; $i += 2) { [BitConverter]::ToUInt16($b, $i) }
    try { (Get-Date -Year $u[0] -Month $u[1] -Day $u[3] -Hour $u[4] -Minute $u[5] -Second $u[6]).ToString('dd.MM.yyyy HH:mm:ss') }
    catch { '' }
}

# Profiles: имя, категория, даты. Signatures: то же имя + MAC шлюза и описание адаптера.
$profiles = @{}
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles' -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    $profiles[$p.ProfileName] = [pscustomobject]@{
        Guid      = $_.PSChildName
        Created   = ToDate $p.DateCreated
        LastConn  = ToDate $p.DateLastConnected
        Managed   = $p.Managed
    }
}

'=== Профили и сигнатуры (MAC шлюза — единственный твёрдый признак «чья сеть») ==='
foreach ($root in 'Unmanaged', 'Managed') {
    Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\$root" -ErrorAction SilentlyContinue | ForEach-Object {
        $s = Get-ItemProperty $_.PSPath
        $pr = $profiles[$s.Description]
        ''
        "  Профиль        : {0}" -f $s.Description
        "  Тип сигнатуры  : {0}" -f $root
        # MAC лежит бинарником — без ручного форматирования печатается как System.Byte[]
        $mac = if ($s.DefaultGatewayMac) { ($s.DefaultGatewayMac | ForEach-Object { $_.ToString('X2') }) -join ':' } else { '<нет>' }
        "  MAC шлюза      : {0}" -f $mac
        "  DNS-суффикс    : {0}" -f $s.DnsSuffix
        "  Первая сеть    : {0}" -f $s.FirstNetwork
        if ($pr) {
            "  Профиль создан : {0}" -f $pr.Created
            "  Последний конн.: {0}" -f $pr.LastConn
        }
    }
}

''
'=== Журнал NetworkProfile: когда к какой сети подключались (Id 10000/10001) ==='
$ev = @(Get-WinEvent -FilterHashtable @{
        LogName = 'Microsoft-Windows-NetworkProfile/Operational'
        Id      = 10000, 10001
    } -MaxEvents 400 -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
if (-not $ev) { '  журнал пуст или отключён' }
foreach ($e in $ev) {
    $x = [xml]$e.ToXml(); $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $act = if ($e.Id -eq 10000) { 'ПОДКЛЮЧЕНА' } else { 'отключена ' }
    "  {0:dd.MM.yyyy HH:mm:ss}  {1}  имя={2}  описание={3}" -f $e.TimeCreated, $act, $d['Name'], $d['Description']
}

''
'=== Журнал NCSI / NlaSvc — смена сетевого расположения ==='
$nla = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'Microsoft-Windows-NetworkProfile/Operational'
    } -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin 10000, 10001 } | Sort-Object TimeCreated)
foreach ($e in $nla | Select-Object -Last 40) {
    $m = ($e.Message -replace '\s+', ' ').Trim()
    if ($m.Length -gt 110) { $m = $m.Substring(0, 110) }
    "  {0:dd.MM.yyyy HH:mm:ss}  Id={1,-6} {2}" -f $e.TimeCreated, $e.Id, $m
}

''
'=== Сетевые адаптеры: MAC самой машины и текущий шлюз ==='
Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' -ErrorAction SilentlyContinue |
    ForEach-Object {
        "  {0}" -f $_.Description
        "     MAC машины  : {0}" -f $_.MACAddress
        "     IP          : {0}" -f ($_.IPAddress -join ', ')
        "     Шлюз        : {0}" -f ($_.DefaultIPGateway -join ', ')
        "     DHCP-сервер : {0}" -f $_.DHCPServer
    }
'  ARP-таблица (MAC текущего шлюза для сверки):'
Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.State -in 'Reachable', 'Stale', 'Permanent' -and $_.IPAddress -notmatch '^(224|239|255)' } |
    Select-Object -First 15 IPAddress, LinkLayerAddress, State |
    Format-Table -Auto | Out-String -Width 100
