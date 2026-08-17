[Console]::OutputEncoding = [Text.Encoding]::UTF8
# КОГДА И ГДЕ машина вырубилась: привязка каждого Kernel-Power 41 к моменту отказа и к сети.
#
# Грабля (СЗ 161346, бэклог п.161): KP41 пишется ПРИ ЗАГРУЗКЕ и описывает предыдущий сеанс,
# поэтому «дата события» — это дата, когда машину подняли, а не когда она упала. Момент отказа
# ищется отдельно: последнее событие в журнале перед стартом. А место — по сети, к которой
# машина была подключена в том же сеансе (дом клиента и сервис дают разные профили).
# Без этого 8 вырубонов, случившихся в сервисе, ушли клиенту в письмо как «за время Вашей
# эксплуатации» — и по трём из них место всё равно осталось недоказанным (сети не было вообще).
#
# Использование: bash tools/recipes/host/ssh-run.sh tools/recipes/client/kp41-where.ps1 <IP>

$Days = 30
$since = (Get-Date).AddDays(-$Days)

# Сетевые подключения: по ним определяем, в чьей сети шёл сеанс
$net = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-NetworkProfile/Operational'
        Id        = 10000
        StartTime = $since
    } -ErrorAction SilentlyContinue | ForEach-Object {
        $x = [xml]$_.ToXml(); $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        if ($d['Name'] -notmatch 'Триває|Identifying|Неідент') {
            [pscustomobject]@{ Time = $_.TimeCreated; Name = $d['Name'] }
        }
    } | Sort-Object Time)

# Старты ОС — границы сеансов
$boots = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        Id           = 12
        ProviderName = 'Microsoft-Windows-Kernel-General'
        StartTime    = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated | Select-Object -ExpandProperty TimeCreated)

$kp41 = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        Id           = 41
        ProviderName = 'Microsoft-Windows-Kernel-Power'
        StartTime    = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

"Kernel-Power 41 за $Days дн.: $($kp41.Count)"
''
'Каждая строка: КОГДА подняли → каким был упавший сеанс → в какой сети он шёл'
''

foreach ($e in $kp41) {
    $x = [xml]$e.ToXml(); $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $bug = [int]$d['BugcheckCode']
    $btn = $d['PowerButtonTimestamp']
    # Начало упавшего сеанса — предыдущий старт ОС до момента этой загрузки
    $bootOfCrashed = $boots | Where-Object { $_ -lt $e.TimeCreated.AddSeconds(-30) } | Select-Object -Last 1
    # Момент отказа — последнее событие в System до текущей загрузки
    $last = Get-WinEvent -FilterHashtable @{
            LogName   = 'System'
            StartTime = $e.TimeCreated.AddHours(-48)
            EndTime   = $e.TimeCreated.AddSeconds(-20)
        } -MaxEvents 1 -ErrorAction SilentlyContinue
    #
    $kind = if ($btn -and $btn -ne '0') { 'КНОПКА' }
            elseif ($bug -ne 0) { "BSOD 0x{0:x}" -f $bug }
            else { 'hard-off' }
    #
    # Сеть упавшего сеанса: последнее подключение в его границах
    $netInSession = $null
    if ($bootOfCrashed) {
        $netInSession = $net | Where-Object { $_.Time -ge $bootOfCrashed -and $_.Time -le $e.TimeCreated } |
            Select-Object -ExpandProperty Name -Unique
    }
    $where = if ($netInSession) { ($netInSession -join ' + ') } else { 'сети не было — место НЕ определено' }
    #
    $dur = if ($bootOfCrashed -and $last) { '{0:N0} мин' -f ($last.TimeCreated - $bootOfCrashed).TotalMinutes } else { '?' }
    #
    $from = if ($bootOfCrashed) { '{0:dd.MM HH:mm:ss}' -f $bootOfCrashed } else { '?' }
    $to = if ($last) { '{0:dd.MM HH:mm:ss}' -f $last.TimeCreated } else { '?' }
    "{0:dd.MM HH:mm:ss}  {1,-12}" -f $e.TimeCreated, $kind
    "     упавший сеанс : {0}  ->  {1}   (прожил {2})" -f $from, $to, $dur
    "     сеть сеанса   : {0}" -f $where
}
