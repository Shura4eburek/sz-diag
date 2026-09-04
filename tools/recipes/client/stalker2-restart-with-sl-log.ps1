$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Перезапуск игры с включённым логом Streamline — решающая проверка по FG (СЗ 123456).
#
# Зачем: PresentMon показал displayed == presented, то есть кадры не генерируются, хотя в конфиге
# Method=DLSSG/On2x и модули DLSS-G загружены. Настройку применяли ВНУТРИ запущенной сессии
# (игра стартовала в 23:23, конфиг переписан в 23:33) — смена бэкенда FG на лету может не
# примениться. Рестарт разделяет две версии: «нужен перезапуск» и «DLSS-G отвергается всегда».
# Лог Streamline (sl.interposer.json рядом с exe) в обоих случаях назовёт причину отказа.
#
# ВАЖНО: запускать через  szcli exec <СЗ> -f <этот файл> --in-session  — иначе игра стартует
# в session 0 без десктопа и не покажет картинку.
# Откат: удалить sl.interposer.json (см. stalker2-cleanup-sl-log.ps1).
#
#   szcli exec 123456 -f tools\recipes\client\stalker2-restart-with-sl-log.ps1 --in-session

$ErrorActionPreference = 'SilentlyContinue'
$bin     = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\Stalker2\Binaries\Win64'
$logDir  = 'C:\Users\nekit\AppData\Local\Temp\sl-logs'
$appId   = '1643320'

'=== 1. включаю лог Streamline ==='
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$json = @{
    showConsole = $false
    logLevel    = 2
    logPath     = $logDir
} | ConvertTo-Json
Set-Content -Path (Join-Path $bin 'sl.interposer.json') -Value $json -Encoding UTF8
'   {0}\sl.interposer.json → logPath {1}' -f $bin, $logDir

'=== 2. закрываю игру ==='
$procs = Get-Process Stalker2, Stalker2-Win64-Shipping
foreach ($p in $procs) {
    '   {0} pid {1}: закрываю' -f $p.ProcessName, $p.Id
    $null = $p.CloseMainWindow()
}
Start-Sleep -Seconds 8
foreach ($p in (Get-Process Stalker2, Stalker2-Win64-Shipping)) {
    '   {0} pid {1}: не закрылся по окну, снимаю' -f $p.ProcessName, $p.Id
    Stop-Process -Id $p.Id -Force
}
Start-Sleep -Seconds 5
'   осталось процессов игры: {0}' -f (Get-Process Stalker2, Stalker2-Win64-Shipping).Count

'=== 3. запускаю заново через Steam ==='
Start-Process "steam://rungameid/$appId"
Start-Sleep -Seconds 45
$new = Get-Process Stalker2-Win64-Shipping
if ($new) { '   игра поднялась: pid {0}, старт {1:HH:mm:ss}' -f $new.Id, $new.StartTime }
else      { '   игра ещё не поднялась (Steam мог показать окно запуска) — проверить отдельно' }
