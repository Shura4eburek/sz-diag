$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разбор срывов видеодвижка (LiveKernelEvent 0x141 / 0x1B8 / 0x1A8) — сколько, когда,
# и сыплются ли они в ПРОСТОЕ, а не только при старте драйверов и входе в сессию.
#
# Грабля (163194): в журнале 1919 записей LiveKernelEvent, из них «891 x 0x141
# VIDEO_ENGINE_TIMEOUT_DETECTED» — и это НЕ 891 сбой. Очередь WER переотправляет одни и те
# же дампы при каждом входе в сессию: реальных дампов на диске оказалось 6. Считать частоту
# по записям журнала = записать в дефект то, чего не было. Поэтому здесь всё сводится к
# уникальным дампам, а число записей печатается отдельно и явно помечено как не-частота.
# Полноценных TDR (4101/4098) при этом может не быть вовсе — обычная проверка
# «падал ли видеодрайвер» покажет чисто, поэтому смотрим именно живые дампы.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-timeout-history.ps1 --timeout 300

$ErrorActionPreference = 'Continue'

$names = @{
    '0x141' = 'VIDEO_ENGINE_TIMEOUT_DETECTED'
    '0x1B8' = 'WATCHDOG_LIVEDUMP_DXGK'
    '0x1A8' = 'WATCHDOG_LIVEDUMP'
    '0x117' = 'VIDEO_TDR_TIMEOUT_DETECTED'
    '0x119' = 'VIDEO_SCHEDULER_INTERNAL_ERROR'
}

'== LiveKernelEvent (Application, Id=1001): по дням и кодам'
# Структура WER-события Id=1001: [2]=тип отчёта, [5]=код (hex без 0x), [15]=список файлов
# отчёта (первым — путь к дампу). ВРЕМЯ СОБЫТИЯ != время сбоя: очередь WER переотправляет
# один и тот же дамп при каждом логоне, поэтому 5 дампов дают сотни «событий» (163194:
# 1919 записей на 6 реальных дампов). Считаем по УНИКАЛЬНЫМ дампам, а не по записям.
$ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1001 } -ErrorAction SilentlyContinue |
        Where-Object { $_.Properties.Count -gt 15 -and $_.Properties[2].Value -eq 'LiveKernelEvent' })
if (-not $ev) { 'LiveKernelEvent не найдено'; return }

$rows = foreach ($e in $ev) {
    $code = '0x' + ([string]$e.Properties[5].Value).ToUpper()
    $files = [string]$e.Properties[15].Value
    $dump = ($files -split "`n" | Where-Object { $_ -match '\.dmp' } | Select-Object -First 1)
    if ($dump) { $dump = $dump.Trim().Split([char]92)[-1] }   # имя файла без пути (char 92 = обратный слеш)
    [pscustomobject]@{ Час = $e.TimeCreated; Код = $code; Дамп = $dump }
}

$uniq = @($rows | Where-Object Дамп | Group-Object Дамп | ForEach-Object {
    $g = $_.Group | Sort-Object Час | Select-Object -First 1
    [pscustomobject]@{ Дамп = $_.Name; Код = $g.Код; Перше = $g.Час; Повідомлень = $_.Count }
})
"записей в журнале: $($rows.Count) — но это ретрансляции WER, не отдельные сбои"
"УНИКАЛЬНЫХ дампов (= реальных срывов): $($uniq.Count)"
''
'-- реальные срывы (по уникальным дампам)'
$uniq | Sort-Object Перше -Descending | ForEach-Object {
    $n = $names[$_.Код]; if (-not $n) { $n = '(неизвестный код)' }
    '   {0}  {1} {2}   дамп {3}  (в журнале продублирован {4} раз)' -f $_.Перше.ToString('yyyy-MM-dd HH:mm'), $_.Код, $n, $_.Дамп, $_.Повідомлень
}
''
'-- записей журнала по кодам (для сравнения, НЕ частота дефекта)'
$rows | Group-Object Код | Sort-Object Count -Descending | ForEach-Object {
    $n = $names[$_.Name]; if (-not $n) { $n = '(неизвестный код)' }
    '   {0} {1} — {2} записей' -f $_.Name, $n, $_.Count
}

''
'== по дням (последние 30 дней с событиями)'
$rows | Group-Object { $_.Час.ToString('yyyy-MM-dd') } | Sort-Object Name -Descending |
    Select-Object -First 30 | ForEach-Object {
        '   {0}  {1,4} шт   {2}' -f $_.Name, $_.Count, (($_.Group | Group-Object Код | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ')
    }

''
'== сыплются ли В ПРОСТОЕ (не в первые 5 мин после загрузки и не в 3 мин вокруг логона)'
$boots = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Boot'; Id = 20 } -MaxEvents 200 -ErrorAction SilentlyContinue |
           Select-Object -ExpandProperty TimeCreated)
if (-not $boots) { $boots = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005 } -MaxEvents 200 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty TimeCreated) }
$logons = @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4624 } -MaxEvents 400 -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty TimeCreated)

$idle = @($rows | Where-Object {
    $t = $_.Час
    $nearBoot = $boots | Where-Object { $t -ge $_ -and ($t - $_).TotalMinutes -le 5 }
    $nearLogon = $logons | Where-Object { [math]::Abs(($t - $_).TotalMinutes) -le 3 }
    -not $nearBoot -and -not $nearLogon
})
"   вне старта и логона: $($idle.Count) из $($rows.Count)"
$idle | Sort-Object Час -Descending | Select-Object -First 15 | ForEach-Object {
    '      {0}  {1}' -f $_.Час.ToString('yyyy-MM-dd HH:mm:ss'), $_.Код
}

''
'== nvidia-smi: линк PCIe, VBIOS, температура, счётчики'
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (Test-Path $smi) {
    & $smi --query-gpu=name,vbios_version,pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max,temperature.gpu,power.draw,clocks.current.graphics --format=csv
    ''
    '   -- Xid / ошибки шины (nvidia-smi -q, выжимка)'
    (& $smi -q) 2>$null | Select-String -Pattern 'Replay|Xid|ECC Errors|Retired|Remapped|Pending|Bus Id|Link Speed|Link Width|Reset Required' | ForEach-Object { '   ' + $_.Line.Trim() }
} else { '   nvidia-smi не найден' }

''
'== мониторы, которые винда видит ПРЯМО СЕЙЧАС'
$techMap = @{ 0='VGA'; 4='DVI'; 5='HDMI'; 10='DP'; 11='DP(вбуд)'; 15='Miracast'; 2147483648='Internal' }
$ids = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue)
$conn = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction SilentlyContinue)
if (-not $ids) { '   мониторов не видит вообще' }
foreach ($m in $ids) {
    $name = -join ($m.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $sn = -join ($m.SerialNumberID | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $c = $conn | Where-Object { $_.InstanceName -eq $m.InstanceName } | Select-Object -First 1
    $t = if ($c) { $techMap[[int64]$c.VideoOutputTechnology] } else { '?' }
    if (-not $t) { $t = "код $($c.VideoOutputTechnology)" }
    '   {0} / SN {1} / {2}' -f $name, $sn, $t
}

''
'== журнал Display / nvlddmkm (последние 15 записей уровня Error/Warning)'
foreach ($p in 'Display', 'nvlddmkm') {
    $x = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = $p } -MaxEvents 200 -ErrorAction SilentlyContinue |
           Where-Object { $_.Level -le 3 } | Select-Object -First 15)
    "   [$p] $($x.Count) записей"
    $x | ForEach-Object { '      {0} Id={1} {2}' -f $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $_.Id, ($_.Message -split "`n")[0] }
}
