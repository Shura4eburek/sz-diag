$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ИСТОРИЯ ОТВАЛОВ СЕТЕВОГО ЛИНКА ПО ЖУРНАЛУ: отваливалась ли сетевуха ДО того, как машина
# приехала в сервис, и как часто.
#
# Грабля (СЗ 162367): вахта net-watch.ps1 отвечает только за то окно, пока она стоит —
# два чистых часа в боксе ничего не говорят про месяц у клиента. А жалоба «відвалюється»
# проверяется именно журналом: NDIS пишет каждый разрыв линка отдельным событием с
# указанием адаптера, и это единственный способ увидеть частоту дефекта задним числом.
# Отдельно нужен DHCP: линк может стоять, а адреса машина не получать — для загрузки по
# PXE это тот же самый «не стартує», но причина в их сервере, а не в плате.
#
#   szcli exec <СЗ> -f tools\recipes\client\net-link-history.ps1
# В WinPE смысла не имеет: журнала клиентской винды там нет (грузиться в её систему либо
# читать журнал офлайн — pe-offline-events.ps1).

$Days = 30    # ← глубина разбора
$Tail = 60    # ← сколько последних событий печатать построчно

$since = (Get-Date).AddDays(-$Days)
"окно: последние $Days дн. (с {0:dd.MM HH:mm})" -f $since

# NDIS/Operational по умолчанию включён и переживает ребуты — основной источник.
# System нужен как дубль: драйверы Intel/Realtek пишут разрывы ещё и туда, а на части
# сборок NDIS-канал выключен, и тогда единственная история остаётся в System.
$src = @(
    @{ Log = 'Microsoft-Windows-NDIS/Operational';        Ids = @(10400, 10401, 10402, 10403); Tag = 'NDIS' },
    @{ Log = 'Microsoft-Windows-Dhcp-Client/Operational'; Ids = @(1001, 1002, 1003, 1005, 1006); Tag = 'DHCP' },
    @{ Log = 'Microsoft-Windows-NetworkProfile/Operational'; Ids = @(10000, 10001); Tag = 'ПРОФИЛЬ' },
    @{ Log = 'System'; Ids = @(); Tag = 'SYSTEM' }
)

# Канал выключен и канал пустой — РАЗНЫЕ вещи, и путать их нельзя: NDIS/Operational и
# Dhcp-Client/Operational на клиентской винде выключены по умолчанию (проверено на боксе),
# так что «нет событий» там означает «истории не велось», а не «разрывов не было».
# Прочитать это как «сеть чистая» = отдать заявку с ложным «дефект не подтвердился».
function Log-State($name) {
    $l = Get-WinEvent -ListLog $name -ErrorAction SilentlyContinue
    if (-not $l) { return 'КАНАЛА НЕТ' }
    if (-not $l.IsEnabled) { return 'КАНАЛ ВЫКЛЮЧЕН' }
    return 'включён'
}

$ev = @()
$ndisOff = $false
foreach ($s in $src) {
    $state = Log-State $s.Log
    if ($s.Tag -eq 'NDIS' -and $state -ne 'включён') { $ndisOff = $true }
    $f = @{ LogName = $s.Log; StartTime = $since }
    if ($s.Ids.Count) { $f['Id'] = $s.Ids }
    $raw = @(Get-WinEvent -FilterHashtable $f -ErrorAction SilentlyContinue)
    if ($s.Tag -eq 'SYSTEM') {
        # В System фильтруем по провайдеру: имя драйвера NIC у Intel и Realtek разное,
        # а Tcpip 4198/4199 — конфликт адресов, который для PXE-клуба тоже «не стартує».
        $raw = @($raw | Where-Object {
            $_.ProviderName -match '^(e1[a-z]|Netwtw|rt6|RTK|Realtek|nvnet|NDIS|Tcpip)' -or
            ($_.ProviderName -eq 'Tcpip' -and $_.Id -in 4198, 4199)
        })
    }
    "{0,-9} {1,5} событий   [{2}]  {3}" -f $s.Tag, $raw.Count, $state, $s.Log
    foreach ($r in $raw) {
        $ev += [pscustomobject]@{
            Time = $r.TimeCreated; Tag = $s.Tag; Id = $r.Id
            Prov = $r.ProviderName
            Msg  = (($r.Message -split "`n")[0]).Trim()
        }
    }
}

if ($ndisOff) {
    ''
    'ВНИМАНИЕ: канал NDIS выключен — истории разрывов линка за прошлое НЕТ и не будет.'
    '          Отсутствие событий тут НЕ доказывает, что сетевуха не отваливалась.'
    '          Включить на будущее (оставляет след, снять при закрытии СЗ):'
    '            wevtutil sl Microsoft-Windows-NDIS/Operational /e:true'
    '          За прошлое опираемся на SYSTEM (драйвер NIC) и профили сети ниже.'
    ''
}
if (-not $ev.Count) { 'событий нет — каналы выключены либо журнал подчищен/винда переставлена'; return }
$ev = @($ev | Sort-Object Time)

'--- по дням (сколько разрывов в сутки) ---'
# При выключенном NDIS считаем по отключениям профиля сети: грубее (профиль отваливается
# и при штатном выключении машины), но это единственное, что есть.
$byDay = @($ev | Where-Object { $_.Tag -eq 'NDIS' -and $_.Id -eq 10400 })
$what = 'разрывов линка (NDIS)'
if (-not $byDay.Count) {
    $byDay = @($ev | Where-Object { $_.Tag -eq 'ПРОФИЛЬ' -and $_.Id -eq 10001 })
    $what = 'отключений сети (профиль, грубая оценка)'
}
$byDay | Group-Object { $_.Time.ToString('dd.MM') } |
    ForEach-Object { "{0}  {1}: {2}" -f $_.Name, $what, $_.Count }

# Разрыв интересен в паре с восстановлением: важна не сама пропажа линка (её даёт и
# перезагрузка свитча, и штатное выключение), а сколько машина просидела без сети.
'--- эпизоды «линк упал → поднялся» ---'
$down = $null; $episodes = 0
foreach ($e in $ev) {
    if ($e.Tag -ne 'NDIS') { continue }
    if ($e.Id -eq 10400) { $down = $e.Time }
    elseif ($e.Id -eq 10401 -and $down) {
        $len = [int]($e.Time - $down).TotalSeconds
        "{0:dd.MM HH:mm:ss}  без сети {1,5} с" -f $down, $len
        $down = $null; $episodes++
    }
}
if (-not $episodes) { '  парных эпизодов нет (разрывы без восстановления = выключение машины)' }

'--- ошибки DHCP (адрес не получен: для загрузки по PXE это тот же «не стартует») ---'
$dhcp = @($ev | Where-Object { $_.Tag -eq 'DHCP' })
if ($dhcp.Count) {
    $dhcp | Group-Object Id | ForEach-Object { "  id {0}: {1} раз" -f $_.Name, $_.Count }
} else { '  нет' }

"--- хвост $Tail событий ---"
$ev | Select-Object -Last $Tail | ForEach-Object {
    "{0:dd.MM HH:mm:ss}  {1,-8} {2,-5} {3,-14} {4}" -f $_.Time, $_.Tag, $_.Id, $_.Prov, $_.Msg
}
