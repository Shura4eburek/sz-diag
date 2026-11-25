$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Вариант start-sensors.ps1 через Register-ScheduledTask.
# Грабля (169472, 17.11.2026): агент лежит в "C:\Users\USER\Desktop\updater – home connection\" —
# schtasks /tr с пробелами и en-dash в пути дал Last Result 0x80070002 (файл не найден),
# рецепт молча ответил «НЕ ЗАПУСТИЛСЯ». Register-ScheduledTask передаёт путь без проблем с кавычками.
# Перед запуском — prep-stress.ps1 (C:\OCCT + исключение Defender).
#   szcli exec <СЗ> -f tools\recipes\client\start-sensors-st.ps1 --param Sz=<СЗ>
# exec добавляет преамбулу перед скриптом — param() тут не работает, --param кладёт $Sz сам.
if (-not $Sz) { $Sz = '000000' }

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$lhm = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\lhmmon\lhmmon.exe'),
    (Join-Path $env:ProgramData 'szdiag\tools\lhmmon\lhmmon.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $lhm) { 'lhmmon.exe не найден — сначала szcli push <СЗ> lhmmon'; return }
if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null; 'C:\OCCT создана (откат — cleanup-stress.ps1)' }
"lhmmon: $lhm"
$task = "szdiag-lhm-$Sz"
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
$a = New-ScheduledTaskAction -Execute $lhm -WorkingDirectory (Split-Path $lhm -Parent)
$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $task -Action $a -Principal $p -Settings $s -Force | Out-Null
Start-ScheduledTask -TaskName $task
Start-Sleep -Seconds 25
$pr = Get-Process lhmmon -ErrorAction SilentlyContinue
'процесс: ' + $(if ($pr) { "жив pid=$($pr.Id)" } else { 'НЕ ЗАПУСТИЛСЯ, LastTaskResult=' + (Get-ScheduledTaskInfo $task).LastTaskResult })
if (Test-Path C:\OCCT\sensors.csv) { 'CSV: ' + (Get-Item C:\OCCT\sensors.csv).Length + ' байт' } else { 'CSV нет' }
