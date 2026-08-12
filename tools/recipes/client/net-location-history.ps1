param([int]$Days = 30)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# ГДЕ ФИЗИЧЕСКИ БЫЛА МАШИНА в конкретный день: по сетям, к которым она подключалась.
# Wi-Fi SSID, MAC точки доступа, выданные DHCP-адреса, шлюзы, сетевые профили.
#
# Грабля (СЗ 161346, бэклог п.136): диски переставили местами 29.07 между 12:51 и 13:57,
# шоурум это отрицает («хотіли, але не міняли»), клиент мог крутить сам перед сдачей.
# Спор «кто трогал железо» упирается в «не помню» — а журнал сети говорит, у кого машина
# стояла в это время: домашняя сеть клиента и сеть сервиса дают разные SSID/шлюзы.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\net-location-history.ps1 --timeout 180

$since = (Get-Date).AddDays(-$Days)

'=== Wi-Fi: подключения к сетям (WLAN-AutoConfig 8001) ==='
$wlan = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-WLAN-AutoConfig/Operational'
        Id        = 8001
        StartTime = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

if (-not $wlan) { '  событий нет (Wi-Fi не использовался или журнал пуст)' }
foreach ($e in $wlan) {
    $x = [xml]$e.ToXml(); $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    "{0:dd.MM.yyyy HH:mm:ss}  SSID={1}  BSSID={2}" -f $e.TimeCreated, $d['SSID'], $d['BSSID']
}

''
'=== Проводная сеть: выданные адреса (Dhcp-Client) ==='
# 50067/50068 — предложен/получен адрес; в теле события адрес и сервер
$dhcp = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-Dhcp-Client/Operational'
        StartTime = $since
    } -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -in 50067, 50068, 60000, 1103 } | Sort-Object TimeCreated)

if (-not $dhcp) { '  событий нет (журнал Dhcp-Client пуст или отключён)' }
foreach ($e in $dhcp) {
    $msg = ($e.Message -replace '\s+', ' ').Trim()
    if ($msg.Length -gt 130) { $msg = $msg.Substring(0, 130) }
    "{0:dd.MM.yyyy HH:mm:ss}  Id={1,-6} {2}" -f $e.TimeCreated, $e.Id, $msg
}

''
'=== Сетевые профили (когда какая сеть считалась текущей) ==='
# NetworkProfile хранит имя сети, тип (домашняя/публичная) и время первого/последнего
# подключения — это самый прямой ответ на вопрос «в чьей сети машина стояла в тот день»
$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles'
Get-ChildItem $key -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    # DateCreated/DateLastConnected — SYSTEMTIME в бинарном виде (8 x uint16)
    function ToDate($b) {
        if (-not $b -or $b.Count -lt 16) { return '' }
        $u = for ($i = 0; $i -lt 16; $i += 2) { [BitConverter]::ToUInt16($b, $i) }
        try { (Get-Date -Year $u[0] -Month $u[1] -Day $u[3] -Hour $u[4] -Minute $u[5] -Second $u[6]).ToString('dd.MM.yyyy HH:mm') }
        catch { '' }
    }
    "  {0,-28} категория={1}  создан={2}  последнее подключение={3}" -f `
        $p.ProfileName, $p.Category, (ToDate $p.DateCreated), (ToDate $p.DateLastConnected)
}

''
'=== Текущие адреса и шлюзы ==='
Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' -ErrorAction SilentlyContinue |
    Select-Object Description,
        @{n='IP';e={$_.IPAddress -join ', '}},
        @{n='Шлюз';e={$_.DefaultIPGateway -join ', '}},
        @{n='DHCP-сервер';e={$_.DHCPServer}},
        @{n='Аренда получена';e={$_.DHCPLeaseObtained}} |
    Format-List | Out-String -Width 120
