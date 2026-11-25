# Одним рядком: скільки LiveKernelEvent (141/1B8/…) з моменту boot і час останнього — для поллінгу з хоста.
# Народився на СЗ 169768: спостереження «тестова ОС — чи є тихі TDR кожні ~2 хв».
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
$e = @(Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'; StartTime=$boot} -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' })
$last = if ($e.Count) { '{0} {1}' -f $e[0].TimeCreated.ToString('HH:mm:ss'), (($e[0].Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '') } else { '-' }
$fm = if (Get-Process furmark -EA 0) { 'up' } else { 'down' }
# gpu з CSV датчиків (не викликаємо nvidia-smi — на відвалленій карті він висить); 'NODEV' = карта зникла з шини
$csv = Get-ChildItem C:\ProgramData\szdiag\sensors\*.csv -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 1
$g = if ($csv) { $l = (Get-Content $csv.FullName -Tail 1) -split ';'; if ($l[5] -match 'No devices|failed') { 'NODEV' } elseif ($l[7] -match '^[\d.]+$') { 'ok' } else { $l[5] } } else { '?' }
'TDR={0} last={1} boot={2} furmark={3} gpu={4}' -f $e.Count, $last, $boot.ToString('HH:mm:ss'), $fm, $g
