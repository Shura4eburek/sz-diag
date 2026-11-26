# Можно ли верить времени вырубона из EventLog 6008. Только чтение.
# Грабля (СЗ 168018): интервалы boot -> "обрыв" ровно 40:00 и 1:20:00 с точностью до секунды
# (03.11, 04.11) — похоже, 6008 показывает последнюю отметку "жив", а не момент смерти. Тогда сверка
# дампов 0x141 с временем обрыва по минутам врёт. Рядом с отметкой печатаем последнее реальное
# событие перед загрузкой (System/Application): если оно ПОЗЖЕ отметки — отметка занижена.
# Заодно: глубина журнала System и вся история DxgKrnl-Admin.
#   szcli exec <СЗ> -f tools\recipes\client\outage-stamp-check.ps1 --timeout 180

$clean = { param($s) ([string]$s) -replace '[^\d:.\-/ ]', '' }
$boots = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005 } -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty TimeCreated | Sort-Object)

'== 6008: stamp vs previous boot vs last real event before reboot'
'   next_boot | stamp | up_by_stamp | last_event | event_minus_stamp_or_up_by_event'
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6008 } -ErrorAction SilentlyContinue | Sort-Object TimeCreated | ForEach-Object {
    $t = $_.TimeCreated
    $raw = '{0} {1}' -f (& $clean $_.Properties[0].Value), (& $clean $_.Properties[1].Value)
    $stamp = $null
    try { $stamp = [datetime]::Parse($raw.Trim(), [Globalization.CultureInfo]::GetCultureInfo('uk-UA')) } catch { }
    $end = $t.AddSeconds(-30)
    $prev = $boots | Where-Object { $_ -lt $end } | Select-Object -Last 1
    $last = $null
    foreach ($l in 'System', 'Application') {
        $e = Get-WinEvent -FilterHashtable @{ LogName = $l; EndTime = $end } -MaxEvents 1 -ErrorAction SilentlyContinue
        if ($e -and (-not $last -or $e.TimeCreated -gt $last)) { $last = $e.TimeCreated }
    }
    $up = if ($stamp -and $prev) { ($stamp - $prev).ToString('hh\:mm\:ss') } else { '?' }
    $delta = if ($stamp -and $last) { [int]($last - $stamp).TotalSeconds } else { '?' }
    $upEv = if ($last -and $prev) { ($last - $prev).ToString('hh\:mm\:ss') } else { '?' }
    '   {0:MM-dd HH:mm:ss} | {1} | up {2} | last {3:MM-dd HH:mm:ss} | delta {4}s | up_by_event {5}' -f $t, $raw.Trim(), $up, $last, $delta, $upEv
}

'== log depth'
foreach ($l in 'System', 'Application') {
    $o = Get-WinEvent -LogName $l -Oldest -MaxEvents 1 -ErrorAction SilentlyContinue
    '   {0}: oldest {1:yyyy-MM-dd HH:mm}' -f $l, $o.TimeCreated
}

'== DxgKrnl-Admin: all records'
Get-WinEvent -LogName 'Microsoft-Windows-DxgKrnl-Admin' -ErrorAction SilentlyContinue | Sort-Object TimeCreated | ForEach-Object {
    '   {0:yyyy-MM-dd HH:mm:ss} id {1} L{2} {3}' -f $_.TimeCreated, $_.Id, $_.Level, (($_.Message -split "`r?`n")[0])
}

'== Display / nvlddmkm in System, any level, all history'
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Display', 'nvlddmkm' } -ErrorAction SilentlyContinue | Sort-Object TimeCreated | ForEach-Object {
    '   {0:yyyy-MM-dd HH:mm:ss} {1} id {2} {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -split "`r?`n")[0])
}
