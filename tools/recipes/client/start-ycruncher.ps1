$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# y-cruncher stress — ловец нестабильности контроллера памяти (IMC), бесплатная замена OCCT.
# Породила СЗ 161716: лицензия OCCT протухла ровно в день заявки (бэклог п.152), а дефект —
# классический «7500F + DDR5-6000» (159873, 160176), где TM5 проходит чисто и потому бесполезен.
# VT3/N63 давят IMC сильнее, чем TM5: это не паттерны в ячейках, а сверка результата вычислений.
#
# Три грабли, каждая стоила прогона:
#   1) push кладёт тулы в C:\ProgramData\szdiag\tools, если агент запущен из облачной папки
#      (OneDrive) — путь резолвим по обоим вариантам, иначе «НЕ ЗАПУСТИЛСЯ» без причины (п.151);
#   2) `cmd /c "exe" args > log` — cmd съедает обрамляющие кавычки первого токена и падает с
#      0x1 БЕЗ лога. Нужен двойной уровень: /c ""exe" args > "log"";
#   3) y-cruncher.exe — лаунчер: он выбирает бинарь из Binaries\ под конкретный CPU
#      (на Zen4 7500F это `22-ZN4 ~ Kizuna.exe`), поэтому искать процесс по имени 'y-cruncher'
#      бесполезно — приёмку делать по дочернему процессу и росту лога.
#   szcli exec <СЗ> -f tools\recipes\client\start-ycruncher.ps1
$Sz     = '000000'   # ← номер СЗ
$MemG   = 8          # ← сколько ГБ отдать тесту: оставь ~4 ГБ системе, иначе уйдёт в своп
$DurSec = 300        # ← длительность одного алгоритма
$LimSec = 5400       # ← общий лимит прогона (1.5 часа)
$Algos  = 'VT3 N63 FFTv4'

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent
$yc = @("$base\tools\ycruncher\y-cruncher.exe", 'C:\ProgramData\szdiag\tools\ycruncher\y-cruncher.exe') |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $yc) { throw "y-cruncher.exe не найден — сначала szcli push $Sz ycruncher" }
"y-cruncher: $yc"

if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null }
$log = 'C:\OCCT\ycruncher.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue

$ycArgs = ('stress -M:{0}G -D:{1} -TL:{2} {3}' -f $MemG, $DurSec, $LimSec, $Algos)
"команда: $ycArgs"

# Задача под SYSTEM: тест консольный, рендер не нужен, а процесс из сессии агента
# умирает вместе с exec'ом, когда SSH/канал задавлен нагрузкой.
$task = "szdiag-yc-$Sz"
$cmd  = ('/c ""{0}" {1} > "{2}" 2>&1"' -f $yc, $ycArgs, $log)
schtasks /delete /tn $task /f 2>$null | Out-Null
$action    = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $cmd -WorkingDirectory (Split-Path $yc -Parent)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task
"задача $task запущена"

# Приёмка: нагрузка засчитывается по живому дочернему процессу и растущему логу,
# а не по факту «задача создана» — она рапортует успех и на мёртвом тесте.
Start-Sleep -Seconds 45
$kid = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\Binaries\*' } | Select-Object -First 1
'бинарь: ' + $(if ($kid) { "$($kid.Name) pid=$($kid.ProcessId), память $([int]($kid.WorkingSetSize/1MB)) МБ" } else { 'НЕ ЗАПУСТИЛСЯ' })
if (Test-Path $log) {
    "лог: {0:N0} б" -f (Get-Item $log).Length
    Get-Content $log -Encoding UTF8 | Select-Object -Last 12
} else { 'лога нет — смотри LastTaskResult задачи (task-why.ps1)' }
