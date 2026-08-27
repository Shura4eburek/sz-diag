# Запустить программу в ИНТЕРАКТИВНОЙ сессии пользователя, когда агент живёт под SYSTEM.
# Грабля (123123, 2026-08-27): после ребута агент поднимается автостарт-задачей под
# NT AUTHORITY\СИСТЕМА в session 0. Оттуда GUI не существует, и всё ломается МОЛЧА:
#   - `setup.exe /auto upgrade` стартует и мгновенно умирает, папки $WINDOWS.~BT не появляются;
#   - снимок экрана падает Win32Exception в CopyFromScreen;
#   - любое окно/сообщение пользователь не видит.
# До ребута тот же exec шёл под kiril в session 1 и всё работало — отсюда «то работает, то нет».
# Обход: транзиентная scheduled task с LogonType Interactive от имени залогиненного пользователя.
# Запуск: szcli exec <СЗ> -f tools\recipes\client\run-in-session.ps1   (правь $Exe/$Args ниже)
param(
    [string]$Exe = 'D:\setup.exe',
    [string]$Args = '/auto upgrade /eula accept /dynamicupdate disable /migratedrivers all /showoobe none /compat ignorewarning',
    [string]$TaskName = 'szdiag-run-in-session',
    [switch]$KeepTask
)
$sessionUser = (quser 2>$null | Select-Object -Skip 1 | ForEach-Object { ($_ -replace '^\s*>?', '').Split(' ')[0] } | Select-Object -First 1)
if (-not $sessionUser) { throw 'нет активной сессии пользователя — интерактивно запускать некуда' }
$full = ($env:COMPUTERNAME + '\' + $sessionUser)
('сессия: ' + $full)
$act = New-ScheduledTaskAction -Execute $Exe -Argument $Args
$pr  = New-ScheduledTaskPrincipal -UserId $full -LogonType Interactive -RunLevel Highest
$st  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$null = Register-ScheduledTask -TaskName $TaskName -Action $act -Principal $pr -Settings $st -Force
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 10
# SessionId 0 = процесс не увидит ни один пользователь; ждём SessionId >= 1
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object Name, Id, SessionId | Format-Table -Auto | Out-String
if (-not $KeepTask) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue }
