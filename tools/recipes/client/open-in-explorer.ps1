# Открыть путь в Проводнике на экране клиента, когда агент живёт под SYSTEM (session 0).
# Грабля (111111, 2026-09-03): `Start-Process explorer.exe` из exec-сессии агента стартует в
# session 0 — окна пользователь не видит вообще, при этом команда рапортует успех.
# Обход: транзиентная scheduled task с LogonType Interactive от залогиненного пользователя.
# Уровень — Limited, а не Highest: elevated explorer окно не открывает (передаёт команду
# уже запущенному unelevated-процессу и молча выходит).
# Правь $Path под задачу. Запуск: szcli exec <СЗ> -f tools\recipes\client\open-in-explorer.ps1
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$Path = 'D:\'
$TaskName = 'szdiag-open-explorer'

if (-not (Test-Path -LiteralPath $Path)) { throw "путь не существует на клиенте: $Path" }

# Имя пользователя — из Win32_ComputerSystem: из session 0 вывод quser идёт без маркера '>',
# и парсер отдаёт пустоту (бэклог п.220).
$full = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $full) {
    $line = quser 2>$null | Select-Object -Skip 1 | Select-Object -First 1
    if ($line) { $full = $env:COMPUTERNAME + '\' + ($line -replace '^\s*>?', '').Split(' ')[0] }
}
if (-not $full) { throw 'нет активной сессии пользователя — открывать окно некому' }
"сессия: $full"

$act = New-ScheduledTaskAction -Execute 'explorer.exe' -Argument $Path
$pr  = New-ScheduledTaskPrincipal -UserId $full -LogonType Interactive -RunLevel Limited
$st  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(5)) -MultipleInstances IgnoreNew
$null = Register-ScheduledTask -TaskName $TaskName -Action $act -Principal $pr -Settings $st -Force
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 5

$info = Get-ScheduledTaskInfo -TaskName $TaskName
"LastTaskResult: 0x{0:X}" -f $info.LastTaskResult
Get-Process -Name explorer -ErrorAction SilentlyContinue |
    Select-Object Id, SessionId | Format-Table -AutoSize | Out-String -Width 80

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
