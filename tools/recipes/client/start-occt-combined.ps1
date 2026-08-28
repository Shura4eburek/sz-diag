$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЗАПУСК OCCT Combined С ГАРАНТИРОВАННЫМ ОТЧЁТОМ (задача под SYSTEM, рук у машины не нужно).
#
# Пара к start-occt-mem.ps1, но для контрольного прогона всей сборки: CPU + Linpack +
# Memory + GPU + VRAM. Нужен там, где результат прогона уходит клиенту ПИСЬМЕННО.
#
# Грабля, которая его породила (СЗ 161346, 26.08.2026): контрольный прогон после замены
# платы гнали интерактивным окном OCCT, а в 17:45 окно закрыли руками, чтобы освободить
# машину под дисковый тест. Итог:
#  - подтесты НЕ умерли — linpack и gpu3d молотили ещё полтора часа (нашли в 19:13),
#    и всё это время машина по индикатору стресса выглядела свободной;
#  - счётчик ошибок видел только оператор на экране («0» на 17:45), машиночитаемого
#    отчёта не осталось вообще — а клиенту он обещан письменно (его условие 5.3).
# Задача под SYSTEM с --auto-save-report + --auto-close закрывает обе: отчёт пишется сам,
# процесс гасится сам по концу расписания.
#
# Приёмка — по загрузке CPU и занятой памяти сразу после старта, а не по «задача запущена».
#
#   szcli exec <СЗ> -f tools\recipes\client\make-combined-schedule.ps1   # расписание
#   szcli exec <СЗ> -f tools\recipes\client\start-occt-combined.ps1      # старт
#   szcli exec <СЗ> -f tools\recipes\client\check-occt-result.ps1        # итог

$Sz       = '161346'                   # ← номер СЗ
$Schedule = 'schedule-combined.json'   # ← расписание (make-combined-schedule.ps1)
$Tag      = 'EXPO6000'                 # ← метка конфигурации в имя отчёта
$Suffix   = 'comb'                     # ← в имя задачи: szdiag-occt<Suffix>-<СЗ>

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден — не от чего считать путь к tools\occt'; return }
$occt = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'),
    (Join-Path $env:ProgramData 'szdiag\tools\occt')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $occt) { 'tools\occt нет — сначала szcli push <СЗ> occt'; return }

# Лицензия с «(2)» в имени = OCCT её не видит (грабля 162367).
$good = Join-Path $occt 'license.oke'
if (-not (Test-Path $good)) {
    $bad = Get-ChildItem $occt -Filter '*.oke' | Select-Object -First 1
    if ($bad) { Copy-Item $bad.FullName $good -Force; "лицензия: скопировал $($bad.Name) -> license.oke" }
    else { 'ВНИМАНИЕ: .oke не найден — OCCT пойдёт без лицензии' }
}

$sched = Join-Path $occt $Schedule
if (-not (Test-Path $sched)) { "нет расписания $sched — сначала make-combined-schedule.ps1"; return }

New-Item -ItemType Directory -Path 'C:\OCCT' -Force | Out-Null
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = "C:\OCCT\combined-$Tag-$stamp.html"   # рядом лягут .json и .csv — json удобнее парсить
$task   = "szdiag-occt$Suffix-$Sz"

Get-Process OCCTCmd, OCCT -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

$argline = 'test --schedule="{0}" --auto-start=true --auto-save-report=true --report-file="{1}" --overwrite-report-file=true --auto-close=true' -f $sched, $report
$action    = New-ScheduledTaskAction -Execute (Join-Path $occt 'OCCTCmd.exe') -Argument $argline -WorkingDirectory $occt
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::FromHours(3))
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $task
"задача $task запущена, отчёт: $report"

Start-Sleep -Seconds 40
$p = Get-Process OCCTCmd -ErrorAction SilentlyContinue
if ($p) { 'OCCTCmd жив: pid {0}, старт {1:HH:mm:ss}' -f $p.Id, $p.StartTime } else { 'ПРОЦЕСС НЕ ЗАПУСТИЛСЯ — смотри task-why.ps1' }
Get-ScheduledTaskInfo -TaskName $task | ForEach-Object { 'LastTaskResult=0x{0:X} (0x41301 = выполняется)' -f $_.LastTaskResult }

# Приёмка: Combined обязан грузить И процессор, И память.
$os  = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
'приёмка: CPU {0} %, свободно памяти {1:N0} МБ из {2:N0} МБ' -f $cpu, ($os.FreePhysicalMemory/1KB), ($os.TotalVisibleMemorySize/1KB)
if ($cpu -lt 50) { 'ВНИМАНИЕ: CPU ниже 50 % — тест не взял нагрузку, проверяй расписание' }
