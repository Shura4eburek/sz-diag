# Розвідка по логах GPU-крашів: TDR (LiveKernelEvent 141/1B8/117), nvlddmkm, WER, Kernel-Power.
# Грабля (СЗ 169768): inline-скрипт у `exec <СЗ> "<ps>"` з szcli.cmd губить подвійні лапки —
# рядки "== ..." виконуються як команди. Тому — тільки через `exec <СЗ> -f gpu-tdr-logs.ps1`.
Write-Output '== LiveKernelReports'
Get-ChildItem C:\Windows\LiveKernelReports -Recurse -File -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 25 FullName,Length,LastWriteTime | Format-Table -AutoSize | Out-String -Width 200
Write-Output '== Minidump'
Get-ChildItem C:\Windows\Minidump -File -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 15 Name,LastWriteTime | Format-Table -AutoSize | Out-String
Write-Output '== nvlddmkm / Display (System)'
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='nvlddmkm','Display'} -MaxEvents 40 -EA 0 |
  Select-Object TimeCreated,ProviderName,Id,@{n='M';e={$m=($_.Message -replace '\s+',' '); $m.Substring(0,[Math]::Min(140,$m.Length))}} | Format-Table -AutoSize -Wrap | Out-String -Width 250
Write-Output '== WER LiveKernelEvent / BlueScreen'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'} -MaxEvents 500 -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' } | Select-Object -First 30 TimeCreated,
  @{n='P1';e={ ($_.Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '' }} | Format-Table -AutoSize | Out-String -Width 200
Write-Output '== Kernel-Power 41 / BugCheck 1001 / 6008'
Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,1001,6008} -MaxEvents 20 -EA 0 | Select-Object TimeCreated,Id,ProviderName | Format-Table -AutoSize | Out-String
Write-Output '== GPU driver'
Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,DriverDate | Format-Table -AutoSize | Out-String
Write-Output '== AppCrash 1000'
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000} -MaxEvents 30 -EA 0 |
  Select-Object TimeCreated,@{n='M';e={ (($_.Message -split "`r?`n")[0..1]) -join ' ' }} | Format-Table -AutoSize -Wrap | Out-String -Width 250
