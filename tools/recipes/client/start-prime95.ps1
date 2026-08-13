$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# prime95 torture (Small FFT) — максимальный тепловой режим CPU, проверка перегрева/троттлинга.
# Породила СЗ 160587: после замены проца надо было увидеть температуры под пиком, а OCCT
# с протухшей лицензией отпал (бэклог п.152). y-cruncher давит IMC, но греет слабее —
# для «перегрев или нет» нужен именно Small FFT с AVX.
#
# Грабли (те же, что у start-ycruncher.ps1, плюс своя):
#   1) prime95 — GUI-приложение: из сессии агента оно умрёт вместе с exec'ом, поэтому
#      запуск только транзиентной задачей под SYSTEM. В session 0 окна не видно, но
#      torture-поток считает нормально; вердикты падают в results.txt рядом с exe;
#   2) без prime.txt со `StressTester=1` первый запуск ждёт ответа в диалоге и висит
#      молча — headless это выглядит как «процесс есть, нагрузки нет»;
#   3) push кладёт тулы либо рядом с агентом, либо в C:\ProgramData\szdiag\tools —
#      резолвим оба пути.
# Приёмка — по живому процессу И реальной загрузке CPU: задача рапортует успех и на
# висящем в диалоге prime95.
#   szcli exec <СЗ> -f tools\recipes\client\start-prime95.ps1
$Sz      = '160587'   # ← номер СЗ
$Threads = 12         # ← потоков (= логических ядер)
$MinFft  = 4          # ← Smallest FFT 4K-32K: минимум памяти, максимум тепла
$MaxFft  = 32
$MinutesPerFft = 3

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent
$dir = @("$base\tools\prime95", 'C:\ProgramData\szdiag\tools\prime95') |
    Where-Object { Test-Path "$_\prime95.exe" } | Select-Object -First 1
if (-not $dir) { throw "prime95.exe не найден — сначала szcli push $Sz prime95" }
"prime95: $dir"

# StressTester=1 + UsePrimenet=0 — иначе первый старт ждёт ответа в диалоге.
# TortureMem=0 = in-place FFT (ОЗУ почти не трогаем: тестируем нагрев CPU, а не память).
@"
StressTester=1
UsePrimenet=0
V24OptionsConverted=1
WorkPreference=0
TortureThreads=$Threads
MinTortureFFT=$MinFft
MaxTortureFFT=$MaxFft
TortureMem=0
TortureTime=$MinutesPerFft
"@ | Set-Content "$dir\prime.txt" -Encoding ASCII

Remove-Item "$dir\results.txt" -Force -ErrorAction SilentlyContinue

$task = "szdiag-p95-$Sz"
schtasks /delete /tn $task /f 2>$null | Out-Null
$action    = New-ScheduledTaskAction -Execute "$dir\prime95.exe" -Argument '-t' -WorkingDirectory $dir
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task
"задача $task запущена"

Start-Sleep -Seconds 40
$p = Get-Process prime95 -ErrorAction SilentlyContinue
'процесс: ' + $(if ($p) { "жив (pid=$($p.Id), память $([int]($p.WorkingSet64/1MB)) МБ)" } else { 'НЕ ЗАПУСТИЛСЯ — смотри task-why.ps1' })

$load = [int](Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 5 |
    ForEach-Object { $_.CounterSamples[0].CookedValue } | Measure-Object -Average).Average
"загрузка CPU: $load %"
if ($load -lt 80) { '⚠ нагрузка не поднялась — prime95 висит в диалоге либо считает одним потоком' }
