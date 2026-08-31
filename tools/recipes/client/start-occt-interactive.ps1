$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЗАПУСК OCCT В ИНТЕРАКТИВНОЙ СЕССИИ — единственный способ прогнать GPU-подтесты.
#
# Грабля (СЗ 161346, 27.08.2026). Combined запустили задачей под SYSTEM
# (start-occt-combined.ps1). Результат: CPU и память грузились честно, а GPU-подтесты
# НЕ СТАРТОВАЛИ ВООБЩЕ — задача под SYSTEM живёт в сессии 0, где нет рабочего стола и
# D3D-устройства. В списке процессов были только OcctMemtest / CpuOcct64 / linpack,
# ни одного gpu3d. Хуже того, OCCTCmd из-за этого не завершился по расписанию: час
# превратился в 1 ч 38 мин без отчёта, пока прогон не сняли руками.
# Признак ровно тот же, что в TESTING.md про GUI-тулзы: без десктопа они висят.
#
# Отсюда правило: MEM/CPU-профили можно гнать под SYSTEM (start-occt-mem.ps1), а всё,
# где есть Gpu3d / GpuUnreal / Vram — только задачей с интерактивным токеном
# залогиненного пользователя. Пользователь должен быть залогинен (query session → Active).
#
# Приёмка — по появлению GPU-процесса, а не по «задача запущена»: нет gpu3d через
# минуту, значит опять гоним половину теста и не знаем об этом.
#
#   szcli exec <СЗ> -f tools\recipes\client\make-combined-schedule.ps1
#   szcli exec <СЗ> -f tools\recipes\client\start-occt-interactive.ps1
#   szcli exec <СЗ> -f tools\recipes\client\check-occt-result.ps1

$Sz       = '161538'
$Schedule = 'schedule-combined.json'
$Tag      = 'EXPO6000-asis'
$Suffix   = 'int'

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден'; return }
$occt = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'),
    (Join-Path $env:ProgramData 'szdiag\tools\occt')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $occt) { 'tools\occt нет — сначала szcli push <СЗ> occt'; return }

# Кто залогинен на консоли: под этим токеном и пойдёт задача.
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { 'НЕТ интерактивной сессии (explorer.exe не найден) — GPU-подтесты не пойдут, нужен залогиненный пользователь'; return }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$user  = '{0}\{1}' -f $owner.Domain, $owner.User
"интерактивный пользователь: $user"

$good = Join-Path $occt 'license.oke'
if (-not (Test-Path $good)) {
    $bad = Get-ChildItem $occt -Filter '*.oke' | Select-Object -First 1
    if ($bad) { Copy-Item $bad.FullName $good -Force; "лицензия: $($bad.Name) -> license.oke" }
    else { 'ВНИМАНИЕ: .oke не найден' }
}

$sched = Join-Path $occt $Schedule
if (-not (Test-Path $sched)) { "нет расписания $sched"; return }

New-Item -ItemType Directory -Path 'C:\OCCT' -Force | Out-Null
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = "C:\OCCT\combined-$Tag-$stamp.html"
$task   = "szdiag-occt$Suffix-$Sz"

Get-Process OCCTCmd, OCCT, OcctMemtest, CpuOcct64, linpack -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

$argline = 'test --schedule="{0}" --auto-start=true --auto-save-report=true --report-file="{1}" --overwrite-report-file=true --auto-close=true' -f $sched, $report
$action    = New-ScheduledTaskAction -Execute (Join-Path $occt 'OCCTCmd.exe') -Argument $argline -WorkingDirectory $occt
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::FromHours(3))
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $task
"задача $task запущена под $user, отчёт: $report"

Start-Sleep -Seconds 60
$all = Get-Process -ErrorAction SilentlyContinue
$gpu = $all | Where-Object { $_.Name -match 'gpu3d|GpuUnreal|Vram|OcctGpu' }
$cpuProc = $all | Where-Object { $_.Name -match 'OcctMemtest|CpuOcct64|linpack' }
'CPU-подтесты: ' + (($cpuProc | ForEach-Object { $_.Name }) -join ', ')
if ($gpu) { 'GPU-подтесты: ' + (($gpu | ForEach-Object { ('{0} (session {1})' -f $_.Name, $_.SessionId) }) -join ', ') }
else { 'GPU-ПОДТЕСТЫ НЕ ЗАПУСТИЛИСЬ — прогон неполный, останавливай и разбирайся' }
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
'приёмка: CPU {0} %, свободно {1:N0} МБ из {2:N0} МБ' -f $cpu, ($os.FreePhysicalMemory/1KB), ($os.TotalVisibleMemorySize/1KB)
