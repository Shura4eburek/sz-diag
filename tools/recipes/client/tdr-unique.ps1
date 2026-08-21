$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Пересчёт LiveKernelEvent по УНИКАЛЬНЫМ инцидентам, а не по строкам журнала.
#
# Грабля (СЗ 161211, 20-21.08.2026): секция `livekernel` дала «8572 события, пачка 0x141 x30
# сегодня» -> написали в kb «TDR-ы воспроизводятся прямо сейчас». Неправда: WER бесконечно
# ретраит очередь ReportQueue (46 папок, пачки ровно по 42), и сегодняшние события ссылаются
# на дамп месячной давности. Реально: 8149 событий = 52 отчёта ~= 20 инцидентов, последний
# 17.07. Отличать инцидент от ретрая нужно по «Идентификатор отчета» и вложенному .dmp.
#
# Запуск: szcli exec <СЗ> -f toolsecipes\client	dr-unique.ps1 --timeout 300
# Бэклог: п.199 (перенести логику в секцию `livekernel` RunDiag).
$ev = Get-WinEvent -FilterHashtable @{LogName='Application';Id=1001} -ErrorAction SilentlyContinue |
      Where-Object { $_.Message -match 'LiveKernelEvent' }
"всего событий 1001/LiveKernelEvent: $($ev.Count)"

$rows = foreach ($e in $ev) {
  $m = $e.Message
  $code = if ($m -match '(?m)^P1:\s*(\S+)') { $Matches[1] } else { '?' }
  $rep  = if ($m -match 'Идентификатор отчета:\s*(\S+)') { $Matches[1] } else { '?' }
  $dmp  = if ($m -match '(WATCHDOG[^\\r\n]*\.dmp)') { $Matches[1] } else { '' }
  [pscustomobject]@{ T=$e.TimeCreated; Code=$code; Rep=$rep; Dmp=$dmp }
}

"`n=== по кодам: всего событий / уникальных отчётов ==="
$rows | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
  "{0,-6} событий {1,6}   уникальных отчётов {2,5}" -f $_.Name, $_.Count, (($_.Group | Select-Object -ExpandProperty Rep -Unique).Count)
}

"`n=== уникальные отчёты по дням (первое появление) ==="
$rows | Group-Object Rep | ForEach-Object { $_.Group | Sort-Object T | Select-Object -First 1 } |
  Group-Object { $_.T.ToString('yyyy-MM') } | Sort-Object Name |
  ForEach-Object { "{0}  уникальных инцидентов: {1}" -f $_.Name, $_.Count }

"`n=== последние 25 уникальных инцидентов (первое появление) ==="
$rows | Group-Object Rep | ForEach-Object { $_.Group | Sort-Object T | Select-Object -First 1 } |
  Sort-Object T | Select-Object -Last 25 |
  ForEach-Object { "{0}  P1={1,-4} {2}" -f $_.T.ToString('yyyy-MM-dd HH:mm'), $_.Code, $_.Dmp }

"`n=== размер очереди WER ==="
(Get-ChildItem 'C:\ProgramData\Microsoft\Windows\WER\ReportQueue' -Directory -ErrorAction SilentlyContinue).Count
