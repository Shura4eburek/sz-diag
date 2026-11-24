# Базова картина для порівняння «тестова ОС vs клієнтська»: ОС, драйвер GPU, пам'ять, мережа, TDR з моменту boot.
# Народився на СЗ 169768: тихі TDR 141 кожні ~2 хв на клієнтській ОС з двома різними GPU — треба розвести софт/залізо.
$os = Get-CimInstance Win32_OperatingSystem
'host={0} os={1} build={2} boot={3}' -f $env:COMPUTERNAME, $os.Caption, $os.BuildNumber, $os.LastBootUpTime
Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,DriverDate,Status | Format-Table -AutoSize | Out-String
Get-CimInstance Win32_PhysicalMemory | Select-Object DeviceLocator,PartNumber,ConfiguredClockSpeed | Format-Table -AutoSize | Out-String
Get-NetAdapter -EA 0 | Select-Object Name,InterfaceDescription,Status,LinkSpeed | Format-Table -AutoSize | Out-String -Width 200
'== monitors present'
Get-PnpDevice -PresentOnly -Class Monitor -EA 0 | Select-Object FriendlyName,Status | Format-Table -AutoSize | Out-String
'== TDR since boot'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'; StartTime=$os.LastBootUpTime} -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' } | Sort-Object TimeCreated | ForEach-Object {
    '{0} {1}' -f $_.TimeCreated, (($_.Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '') }
Get-ChildItem C:\Windows\LiveKernelReports -Recurse -File -EA 0 | Where-Object { $_.LastWriteTime -gt $os.LastBootUpTime } | Select-Object Name,LastWriteTime | Format-Table -AutoSize | Out-String
