$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Запуск программы в интерактивной сессии залогиненного пользователя (видимо на экране).
# Грабля (169472, 17.11.2026): exec --in-session отработал от SYSTEM в сессии 0 — steam -applaunch
# оттуда погасил Steam пользователя, cs2 стартовал невидимым. Задача с LogonType Interactive
# от имени владельца explorer.exe стартует в его сессии без пароля.
#   szcli exec <СЗ> -f run-in-user-session.ps1 --param Exe="C:\...\steam.exe" --param Args="-applaunch 730"
if (-not $Exe) { 'нужен --param Exe=...'; return }
$ex = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $ex) { 'explorer.exe нет — пользователь не залогинен'; return }
$o = Invoke-CimMethod -InputObject $ex -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
"пользователь: $user (сессия $($ex.SessionId))"
$task = 'szdiag-run-user'
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
$a = if ($Args) { New-ScheduledTaskAction -Execute $Exe -Argument $Args } else { New-ScheduledTaskAction -Execute $Exe }
$p = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $a -Principal $p -Force | Out-Null
Start-ScheduledTask -TaskName $task
Start-Sleep -Seconds 5
'LastTaskResult: ' + (Get-ScheduledTaskInfo $task).LastTaskResult
Unregister-ScheduledTask -TaskName $task -Confirm:$false
'задача удалена (процесс живёт дальше)'
