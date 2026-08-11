$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Снять фоновую задачу `szcli exec --detach`, не трогая агента и остальные прогоны.
#
# Грабля (СЗ 161346): у `szcli exec --result <job>` нет способа остановить задачу — а
# отменять приходится (скрипт улетел в фон с багом и 180 минут писал мусор в лог).
# Бить `Get-Process powershell | Stop-Process` нельзя: под тем же именем крутятся
# наблюдатель сенсоров и служебные задачи агента, снесёшь весь прогон.
$JobId = '20260810-181359-4ef81a'   # ← id из вывода `szcli exec --detach`

$found = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$JobId*" })

if ($found.Count -eq 0) { "процесс задачи $JobId не найден (уже завершилась?)"; return }

foreach ($p in $found) {
    "снимаю pid=$($p.ProcessId): $($p.CommandLine.Substring(0, [Math]::Min(120, $p.CommandLine.Length)))"
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Seconds 2
$left = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$JobId*" })
if ($left.Count -eq 0) { "задача $JobId снята" } else { "ВНИМАНИЕ: осталось процессов: $($left.Count)" }
