# Що сталося в момент появи мережі: link-up NIC, TDR, процеси/служби/задачі, що стартували поруч.
# Народився на СЗ 169768: дефект GPU (TDR + вентилятори 100%) ловиться через ~3 с після підключення Ethernet,
# незалежно від відеокарти (5070 і тестова 4070 Ti). Шукаємо, хто прокидається на подію «мережа є».
param([int]$Hours = 3)
$from = (Get-Date).AddHours(-$Hours)
'== boot ' + (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
'== NIC link / NetworkProfile / DHCP (System + NetworkProfile)'
$net = @()
$net += Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$from} -EA 0 | Where-Object { $_.ProviderName -match 'e1|e2f|rt640|Realtek|Intel|NDIS|Tcpip|Dhcp|netw|Kernel-Network' }
$net += Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-NetworkProfile/Operational'; StartTime=$from} -EA 0
$net | Sort-Object TimeCreated | Select-Object TimeCreated,Id,ProviderName,@{n='M';e={ $m = ($_.Message -replace '\s+',' '); if ($m) { $m.Substring(0,[Math]::Min(100,$m.Length)) } }} | Format-Table -AutoSize | Out-String -Width 250
'== TDR / WER'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'; StartTime=$from} -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' } | Sort-Object TimeCreated | ForEach-Object {
    '{0} {1}' -f $_.TimeCreated, (($_.Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '') }
'== nvlddmkm / Display / Kernel-Power'
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$from} -EA 0 | Where-Object { $_.ProviderName -match 'nvlddmkm|Display|Kernel-Power|WHEA' } |
  Sort-Object TimeCreated | Select-Object TimeCreated,Id,ProviderName | Format-Table -AutoSize | Out-String
'== Services started (7036 running) in window'
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Service Control Manager'; Id=7036,7045; StartTime=$from} -EA 0 |
  Sort-Object TimeCreated | Select-Object TimeCreated,Id,@{n='M';e={ ($_.Message -replace '\s+',' ') }} | Format-Table -AutoSize | Out-String -Width 250
'== Processes alive now, started after boot (sorted by start)'
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Get-CimInstance Win32_Process | Where-Object { $_.CreationDate -gt $boot.AddMinutes(2) } | Sort-Object CreationDate |
  Select-Object CreationDate,ProcessId,Name,@{n='Path';e={$_.ExecutablePath}} | Format-Table -AutoSize | Out-String -Width 250
'== Scheduled tasks with network / idle / event triggers, ran in window'
Get-ScheduledTask -EA 0 | ForEach-Object {
  $i = $_ | Get-ScheduledTaskInfo -EA 0
  if ($i -and $i.LastRunTime -gt $from) { '{0} | {1}{2} | last={3} | {4}' -f $i.LastRunTime, $_.TaskPath, $_.TaskName, $i.LastTaskResult, (($_.Actions | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) -join ' ; ') }
} | Sort-Object
'== GPU now'
$smi = 'C:\Windows\System32\nvidia-smi.exe'
if (Test-Path $smi) {
  $j = Start-Job { & 'C:\Windows\System32\nvidia-smi.exe' --query-gpu=name,utilization.gpu,power.draw,temperature.gpu,fan.speed,pstate --format=csv 2>&1 }
  if (Wait-Job $j -Timeout 15) { Receive-Job $j } else { 'nvidia-smi висить (>15 с) — карта, ймовірно, відвалилась'; Stop-Job $j }
}
'== Sensors CSV tail'
Get-ChildItem C:\ProgramData\szdiag\sensors\*.csv -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { $_.FullName; Get-Content $_.FullName -Tail 12 }
