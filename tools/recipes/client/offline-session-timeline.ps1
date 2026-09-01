$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
$all = @(Get-WinEvent -Path 'W:\Windows\System32\winevt\Logs\System.evtx' -ErrorAction SilentlyContinue) | Sort-Object TimeCreated
# 6005 = журнал стартовал (загрузка), 6006 = штатное завершение, 41 = грязный выход.
$marks = @($all | Where-Object { $_.Id -in @(6005,6006,41) })
$boots = @($marks | Where-Object { $_.Id -eq 6005 })
('загрузок (6005): ' + $boots.Count)
''
'старт сессии (UTC)   | длительность | чем закончилась'
'---------------------+--------------+----------------'
for ($i = 0; $i -lt $boots.Count; $i++) {
    $b = $boots[$i]
    $next = if ($i + 1 -lt $boots.Count) { $boots[$i+1].TimeCreated } else { [datetime]::MaxValue }
    # последнее событие этой сессии = последнее до следующей загрузки
    $tail = @($all | Where-Object { $_.TimeCreated -ge $b.TimeCreated -and $_.TimeCreated -lt $next })
    $lastEv = $tail | Select-Object -Last 1
    $clean = @($tail | Where-Object { $_.Id -eq 6006 }).Count -gt 0
    $dirty41 = @($all | Where-Object { $_.Id -eq 41 -and $_.TimeCreated -ge $next.AddMinutes(-6) -and $_.TimeCreated -le $next.AddMinutes(6) }).Count -gt 0
    $dur = $lastEv.TimeCreated - $b.TimeCreated
    $how = if ($clean) { 'штатно' } elseif ($dirty41) { 'ВЫРУБОН' } else { 'обрыв журнала' }
    ('{0} | {1,5:N0} мин     | {2}' -f $b.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), $dur.TotalMinutes, $how)
}
