# Що сталося при останньому вирубоні: KP41 (bugcheck/кнопка), 6008, WHEA, TDR, останні події перед обривом.
# Народився на СЗ 169768: `reboots` показує таймлайн, але не останні події перед обривом і стан GPU-логів у вікні.
param([int]$Hours = 2)
$from = (Get-Date).AddHours(-$Hours)
Write-Output ('== Boot: ' + (Get-CimInstance Win32_OperatingSystem).LastBootUpTime)
Write-Output '== KP41 / 6008 / 1001'
Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,6008,1001; StartTime=$from} -EA 0 | ForEach-Object {
  $x = [xml]$_.ToXml(); $d = @{}; $x.Event.EventData.Data | ForEach-Object { if ($_.Name) { $d[$_.Name] = $_.'#text' } }
  '{0} id={1} BugcheckCode={2} PowerButtonTs={3} Sleep={4} | {5}' -f $_.TimeCreated, $_.Id, $d['BugcheckCode'], $d['PowerButtonTimestamp'], $d['SleepInProgress'], (($_.Message -replace '\s+',' ').Substring(0, [Math]::Min(110, $_.Message.Length)))
}
Write-Output '== WHEA'
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'; StartTime=$from} -EA 0 | Select-Object TimeCreated,Id,LevelDisplayName | Format-Table -AutoSize | Out-String
Write-Output '== WER LiveKernelEvent/BlueScreen'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'; StartTime=$from} -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' } | ForEach-Object {
    '{0} {1}' -f $_.TimeCreated, (($_.Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '') }
Write-Output '== LiveKernelReports / Minidump (window)'
Get-ChildItem C:\Windows\LiveKernelReports, C:\Windows\Minidump -Recurse -File -EA 0 | Where-Object { $_.LastWriteTime -gt $from } | Select-Object FullName,Length,LastWriteTime | Format-Table -AutoSize | Out-String -Width 200
Write-Output '== nvlddmkm/Display'
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='nvlddmkm','Display'; StartTime=$from} -EA 0 | Select-Object TimeCreated,Id | Format-Table -AutoSize | Out-String
Write-Output '== System: last 25 events BEFORE current boot'
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$from; EndTime=$boot.AddSeconds(-5)} -EA 0 | Select-Object -First 25 TimeCreated,Id,ProviderName,LevelDisplayName | Format-Table -AutoSize | Out-String -Width 200
Write-Output '== Application: last 15 events BEFORE current boot'
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$from; EndTime=$boot.AddSeconds(-5)} -EA 0 | Select-Object -First 15 TimeCreated,Id,ProviderName,LevelDisplayName | Format-Table -AutoSize | Out-String -Width 200
