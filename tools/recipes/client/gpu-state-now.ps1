# Стан GPU «у момент дефекту», поки ОС жива: nvidia-smi, PnP-статус, FurMark, TDR за 15 хв, хвіст CSV датчиків.
# Народився на СЗ 169768: при дефекті ОС і агент живі, зображення нема — треба зняти стан до перезавантаження.
$from = (Get-Date).AddMinutes(-15)
Write-Output ('== now ' + (Get-Date -Format 'HH:mm:ss'))
$smi = 'C:\Windows\System32\nvidia-smi.exe'
if (Test-Path $smi) { & $smi --query-gpu=utilization.gpu,power.draw,temperature.gpu,clocks.gr,fan.speed,pstate --format=csv 2>&1 } else { 'nvidia-smi не знайдено' }
Write-Output '== PnP display'
Get-PnpDevice -Class Display -EA 0 | Select-Object FriendlyName,Status,Problem | Format-Table -AutoSize | Out-String
Write-Output '== FurMark'
Get-Process furmark* -EA 0 | Select-Object Name,Id,StartTime,Responding | Format-Table -AutoSize | Out-String
Write-Output '== WER LiveKernelEvent'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Windows Error Reporting'; StartTime=$from} -EA 0 |
  Where-Object { $_.Message -match 'LiveKernelEvent|BlueScreen' } | ForEach-Object {
    '{0} {1}' -f $_.TimeCreated, (($_.Message -split "`r?`n" | Where-Object { $_ -match '^P1:' }) -join '') }
Write-Output '== System warn/err'
Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2,3; StartTime=$from} -EA 0 | Select-Object -First 15 TimeCreated,Id,ProviderName | Format-Table -AutoSize | Out-String -Width 200
Write-Output '== CSV tail'
Get-ChildItem C:\ProgramData\szdiag\sensors\*.csv -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -TotalCount 1; Get-Content $_.FullName -Tail 18 }
