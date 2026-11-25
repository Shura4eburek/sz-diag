# Чи реально навантажена GPU: процес FurMark + nvidia-smi (util/power/temp/clock).
# Народився на СЗ 169768: `sensors status` показує лише кількість рядків CSV, а не те, чи тест іде.
Get-Process furmark* -EA 0 | Select-Object Name,Id,StartTime,@{n='CPUs';e={[int]$_.CPU}} | Format-Table -AutoSize | Out-String
$smi = 'C:\Windows\System32\nvidia-smi.exe'
if (Test-Path $smi) {
  & $smi --query-gpu=utilization.gpu,power.draw,temperature.gpu,clocks.gr,fan.speed,pstate --format=csv
} else { 'nvidia-smi не знайдено' }
Write-Output '== останні рядки CSV датчиків'
Get-ChildItem C:\ProgramData\szdiag\sensors\*.csv -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 3 }
