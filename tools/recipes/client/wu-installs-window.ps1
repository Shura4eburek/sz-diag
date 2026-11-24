# Що ставив Windows Update у вікні (події 19/20/43/44) + свіжі драйвери дисплея з setupapi.
# Народився на СЗ 169768: TDR-шторм 141/1B8 збігся з установкою WU — треба відрізнити підміну драйвера від дефекту.
param([int]$Hours = 2)
$from = (Get-Date).AddHours(-$Hours)
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WindowsUpdateClient'; StartTime=$from} -EA 0 |
  ForEach-Object { '{0} id={1} {2}' -f $_.TimeCreated, $_.Id, (($_.Message -replace '\s+',' ').Substring(0, [Math]::Min(160, $_.Message.Length))) }
Write-Output '== Display drivers now'
Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,DriverDate,Status | Format-Table -AutoSize | Out-String
Write-Output '== setupapi: display/nv/amd installs in window'
$log = 'C:\Windows\INF\setupapi.dev.log'
if (Test-Path $log) {
  Select-String -Path $log -Pattern '>>>  \[Device Install' -Context 0,1 | Select-Object -Last 15 | ForEach-Object { $_.Line; $_.Context.PostContext }
}
