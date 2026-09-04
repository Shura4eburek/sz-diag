$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Снять окна nvidiaProfileInspector, если он запустился в GUI-режиме (СЗ 123456).
#
# Грабля: у nvidiaProfileInspector 3.0.2.1 нет ключа экспорта всей базы — на любой неизвестный
# аргумент он молча открывает окно. При запуске через --in-session эти окна вылезают поверх
# игры пользователя, а Start-Process возвращает пустой ExitCode (процесс не завершился).
# Проверять неизвестные CLI-ключи GUI-инструментов на машине с живым игроком — плохая идея.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvinspector-kill.ps1

$ErrorActionPreference = 'SilentlyContinue'
$procs = Get-Process nvidiaProfileInspector
if (-not $procs) { '   окон инспектора нет'; return }
foreach ($p in $procs) {
    '   снимаю pid {0} (старт {1:HH:mm:ss})' -f $p.Id, $p.StartTime
    Stop-Process -Id $p.Id -Force
}
Start-Sleep -Seconds 2
'   осталось: {0}' -f (Get-Process nvidiaProfileInspector).Count
