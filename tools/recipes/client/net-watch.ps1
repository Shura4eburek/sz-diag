$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ВАХТА ЗА СЕТЕВЫМ АДАПТЕРОМ: ловит момент, когда линк падает или переторговывается вниз.
#
# Грабля (СЗ 162367, ПК-клуб): жалоба «не стартує» + «сетевуха відвалюється» — это ОДНО
# событие, а не два дефекта: система приезжает по PXE, моргнул линк на загрузке — машина
# не стартанула. Проверить это «попинговал — работает» нельзя: отвал ловится только
# временем и трафиком. Плюс типовая картина дефекта — не полный обрыв, а тихая
# переторговка 2.5G → 100 Mbps: связь есть, PXE-образ по ней уже не едет.
#
# Пишет строку с flush — переживает и отвал линка, и вырубон машины.
# Запускать detached, иначе умрёт вместе с ssh/exec-сессией:
#   szcli exec <СЗ> -f tools\recipes\client\net-watch.ps1 --detach
# В WinPE (машина без своей винды) — просто из консоли PE, лог тогда ляжет на X:\.

$Minutes    = 120     # ← сколько держать вахту
$EverySec   = 1       # шаг опроса: отвал на 2-3 с при секундном шаге видно, при 5-секундном уже нет
$Gateway    = ''      # ← пусто = взять шлюз из конфигурации; можно вбить IP руками
$AdapterKey = ''      # ← пусто = активный адаптер; можно кусок имени/описания ('Realtek', 'Ethernet')
$LossToCall = 3       # столько подряд потерянных пингов = считаем связь пропавшей

# В PE каталога ProgramData на системном диске нет, писать некуда — падаем на корень X:.
$LogDir = 'C:\ProgramData\szdiag'
if (-not (Test-Path 'C:\')) { $LogDir = 'X:\szdiag' }
New-Item -ItemType Directory -Path $LogDir -Force -ErrorAction SilentlyContinue | Out-Null
$Log = Join-Path $LogDir ("net-watch-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true    # без этого при отвале/вырубоне теряется ровно то, что нужно

function Say([string]$m) {
    $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m
    $line
    $sw.WriteLine($line)
}

# --- выбор адаптера ------------------------------------------------------------------
# Get-NetAdapter есть не везде (урезанный WinPE), поэтому фоллбэк на WMI. Ошибка «нет
# командлета» иначе выглядит как «нет адаптера», и вахта молча сторожит пустоту.
$hasNetAdapter = [bool](Get-Command Get-NetAdapter -ErrorAction SilentlyContinue)

function Get-Nic {
    if ($hasNetAdapter) {
        $all = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { -not $_.Virtual })
        if ($AdapterKey) { $all = @($all | Where-Object { "$($_.Name) $($_.InterfaceDescription)" -like "*$AdapterKey*" }) }
        $n = $all | Sort-Object { $_.Status -ne 'Up' } | Select-Object -First 1
        if (-not $n) { return $null }
        return [pscustomobject]@{
            Name  = $n.Name
            Desc  = $n.InterfaceDescription
            Drv   = $n.DriverVersion
            Mac   = $n.MacAddress
            Up    = ($n.Status -eq 'Up')
            Speed = $n.LinkSpeed
            Index = $n.ifIndex
        }
    }
    $all = @(Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue |
             Where-Object { $_.PhysicalAdapter -and $_.MACAddress })
    if ($AdapterKey) { $all = @($all | Where-Object { "$($_.NetConnectionID) $($_.Name)" -like "*$AdapterKey*" }) }
    $n = $all | Sort-Object { $_.NetConnectionStatus -ne 2 } | Select-Object -First 1
    if (-not $n) { return $null }
    [pscustomobject]@{
        Name  = $n.NetConnectionID
        Desc  = $n.Name
        Drv   = ''
        Mac   = $n.MACAddress
        Up    = ($n.NetConnectionStatus -eq 2)
        Speed = $(if ($n.Speed) { "{0:N0} Mbps" -f ($n.Speed / 1MB) } else { '?' })
        Index = $n.InterfaceIndex
    }
}

$nic = Get-Nic
if (-not $nic) { Say 'СЕТЕВОГО АДАПТЕРА НЕ ВИДНО — сторожить нечего'; $sw.Close(); return }

# --- шлюз ----------------------------------------------------------------------------
# Шлюз берём ИМЕННО у сторожимого интерфейса. «Первый попавшийся шлюз в системе» — грабля:
# на боксе так подобрался 26.0.0.1 от Radmin VPN при адаптере в 192.168.94.0/24, вахта
# отрапортовала «ПИНГ ПРОПАЛ» на живой сети. Ложный отвал хуже пропущенного.
if (-not $Gateway) {
    if (Get-Command Get-NetIPConfiguration -ErrorAction SilentlyContinue) {
        $Gateway = (Get-NetIPConfiguration -InterfaceIndex $nic.Index -ErrorAction SilentlyContinue).IPv4DefaultGateway.NextHop |
                   Select-Object -First 1
    }
    if (-not $Gateway) {
        $cfg = Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue |
               Where-Object { $_.InterfaceIndex -eq $nic.Index -and $_.DefaultIPGateway }
        if ($cfg) { $Gateway = @($cfg.DefaultIPGateway)[0] }
    }
}

# Пингуем через .NET, а не Test-Connection: командлета может не быть в PE, а тут ещё и
# честный RTT с коротким таймаутом — иначе один зависший пинг растягивает шаг вахты.
$pinger = [Net.NetworkInformation.Ping]::new()
function Ping-Gw {
    if (-not $Gateway) { return -1 }
    try {
        $r = $pinger.Send($Gateway, 900)
        if ($r.Status -eq 'Success') { return [int]$r.RoundtripTime }
    } catch { }
    return -1
}

function Get-Errs {
    if (-not $hasNetAdapter) { return $null }
    $s = Get-NetAdapterStatistics -Name $nic.Name -ErrorAction SilentlyContinue
    if (-not $s) { return $null }
    [pscustomobject]@{
        RxErr  = [int64]$s.ReceivedPacketErrors
        RxDrop = [int64]$s.ReceivedDiscardedPackets
        TxErr  = [int64]$s.OutboundPacketErrors
        TxDrop = [int64]$s.OutboundDiscardedPackets
        RxMB   = [math]::Round($s.ReceivedBytes / 1MB, 1)
        TxMB   = [math]::Round($s.SentBytes / 1MB, 1)
    }
}

$ip = ''
if (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue) {
    $ip = (Get-NetIPAddress -InterfaceIndex $nic.Index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
           Select-Object -First 1).IPAddress
}

Say "СТАРТ вахты на $Minutes мин, шаг $EverySec с"
Say "  адаптер : $($nic.Name) / $($nic.Desc)"
Say "  драйвер : $($nic.Drv)   MAC: $($nic.Mac)"
Say "  линк    : $(if ($nic.Up) { 'UP' } else { 'DOWN' }), скорость $($nic.Speed), IP $ip"
Say "  шлюз    : $(if ($Gateway) { $Gateway } else { 'НЕ НАЙДЕН — пинг не ведём, следим только за линком' })"
$err0 = Get-Errs
if ($err0) { Say "  счётчики на старте: RxErr $($err0.RxErr) RxDrop $($err0.RxDrop) TxErr $($err0.TxErr) TxDrop $($err0.TxDrop)" }

# Опорная скорость — та, с которой линк поднялся. Всё, что ниже неё, событие: тихая
# переторговка 2.5G → 100 Mbps связь не рвёт, поэтому по пингу её не видно вообще.
$baseSpeed = $nic.Speed
$prevUp    = $nic.Up
$prevSpeed = $nic.Speed
$sinceState = Get-Date

$flaps = 0; $downSec = 0; $lossEpisodes = 0; $lossSec = 0; $maxLoss = 0
$lossRun = 0; $lossFrom = $null; $rtts = @()
$deadline = (Get-Date).AddMinutes($Minutes)
$lastBeat = Get-Date

while ((Get-Date) -lt $deadline) {
    $n = Get-Nic
    $now = Get-Date

    if ($n) {
        if ($n.Up -ne $prevUp) {
            $held = [int]($now - $sinceState).TotalSeconds
            if ($n.Up) {
                Say "ЛИНК ПОДНЯЛСЯ, лежал $held с, скорость $($n.Speed)"
                $downSec += $held
            } else {
                Say "ЛИНК УПАЛ (держался $held с)"
                $flaps++
            }
            $prevUp = $n.Up; $sinceState = $now
        }
        if ($n.Up -and $n.Speed -ne $prevSpeed) {
            Say "СКОРОСТЬ ЛИНКА: $prevSpeed -> $($n.Speed)$(if ($n.Speed -ne $baseSpeed) { '  (ниже опорной ' + $baseSpeed + ')' })"
            $prevSpeed = $n.Speed
        }
    } else {
        if ($prevUp) { Say 'АДАПТЕР ПРОПАЛ ИЗ СИСТЕМЫ'; $flaps++; $prevUp = $false; $sinceState = $now }
    }

    $rtt = Ping-Gw
    if ($rtt -ge 0) {
        $rtts += $rtt
        if ($lossRun -ge $LossToCall) {
            $len = [int]($now - $lossFrom).TotalSeconds
            Say "ПИНГ ВЕРНУЛСЯ, связи не было $len с ($lossRun подряд)"
            $lossSec += $len
            if ($lossRun -gt $maxLoss) { $maxLoss = $lossRun }
        }
        $lossRun = 0
    } elseif ($Gateway) {
        if ($lossRun -eq 0) { $lossFrom = $now }
        $lossRun++
        if ($lossRun -eq $LossToCall) {
            $lossEpisodes++
            Say "ПИНГ ПРОПАЛ (линк $(if ($n -and $n.Up) { 'UP' } else { 'DOWN' })) — потеряно $LossToCall подряд"
        }
    }

    # Раз в минуту — отметка живости со счётчиками: без неё в логе после часа тишины
    # непонятно, вахта стояла или всё было хорошо.
    if (($now - $lastBeat).TotalSeconds -ge 60) {
        $e = Get-Errs
        $avg = if ($rtts.Count) { '{0:N1} мс' -f (($rtts | Measure-Object -Average).Average) } else { '—' }
        $cnt = if ($e) { "Rx $($e.RxMB) МБ / Tx $($e.TxMB) МБ, ошибки Rx $($e.RxErr) Tx $($e.TxErr), дискарды Rx $($e.RxDrop) Tx $($e.TxDrop)" } else { 'счётчики недоступны' }
        Say ". линк $(if ($prevUp) { 'UP ' + $prevSpeed } else { 'DOWN' }), пинг ср. $avg, флапов $flaps, эпизодов потери $lossEpisodes | $cnt"
        $lastBeat = $now; $rtts = @()
    }

    Start-Sleep -Seconds $EverySec
}

if (-not $prevUp) { $downSec += [int]((Get-Date) - $sinceState).TotalSeconds }
# Эпизод, начавшийся и не закрывшийся до конца вахты, закрываем руками: иначе самый
# интересный случай — связь пропала и не вернулась — в итоге выглядит как «0 секунд».
if ($lossRun -ge $LossToCall) {
    $lossSec += [int]((Get-Date) - $lossFrom).TotalSeconds
    if ($lossRun -gt $maxLoss) { $maxLoss = $lossRun }
    Say "ПИНГ ТАК И НЕ ВЕРНУЛСЯ до конца вахты ($lossRun подряд)"
}
$e1 = Get-Errs
Say '--- ИТОГ ---'
# RTT под нагрузкой задирает сам тест: на 100-мегабитном канале бокса прокачка подняла пинг
# шлюза до 150-275 мс ОДИНАКОВО и у клиента, и у контрольной вахты на самом боксе (162367).
# Читать это как «сетевуха тупит» — ложный вывод. Вердикт выносится по ПОТЕРЯМ, флапам и
# счётчикам ошибок, а не по средней задержке.
Say 'напоминание: RTT под нагрузкой растёт из-за очереди в канале — судить по потерям и флапам'
Say "флапов линка         : $flaps, суммарно лежал $downSec с"
Say "эпизодов потери пинга: $lossEpisodes, суммарно $lossSec с, худшая серия $maxLoss подряд"
Say "скорость линка       : опорная $baseSpeed, на конец $prevSpeed"
if ($err0 -and $e1) {
    Say ("прирост счётчиков    : RxErr +{0} RxDrop +{1} TxErr +{2} TxDrop +{3}" -f `
        ($e1.RxErr - $err0.RxErr), ($e1.RxDrop - $err0.RxDrop), ($e1.TxErr - $err0.TxErr), ($e1.TxDrop - $err0.TxDrop))
    Say ("прокачано            : Rx {0:N1} МБ / Tx {1:N1} МБ" -f ($e1.RxMB - $err0.RxMB), ($e1.TxMB - $err0.TxMB))
}
# Ноль флапов на простое ничего не доказывает: дефект вылазит на трафике. Пишем это в лог,
# чтобы через неделю никто не прочитал «2 часа чисто» как «сетевуха здорова».
if ($flaps -eq 0 -and $lossEpisodes -eq 0 -and ($e1 -and $err0) -and (($e1.RxMB - $err0.RxMB) -lt 100)) {
    Say 'ВНИМАНИЕ: чисто, но трафика почти не было — прогон на простое отвал не воспроизводит.'
    Say '          Повторить, качая крупный файл/iperf параллельно с вахтой.'
}
Say "лог: $Log"
$sw.Close()
