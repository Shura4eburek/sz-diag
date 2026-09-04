$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Эксперимент: заставить DLSS-G включиться в обход меню игры (СЗ 123456).
#
# Что известно к этому моменту: HAGS включён, файлы DLSS целые и подписанные, DLSS-G в процесс
# загружен, конфиг игры держит Method=DLSSG/Mode=On2x и переживает рестарт, V-Sync выключен,
# лимита кадров нет, PresentMode = Independent Flip. При этом PresentMon показывает
# displayed == presented (87-92 fps) и до, и после перезапуска — генерации нет ни одним бэкендом.
#
# Проверяем два подозрения разом (потом разделим, если сработает):
#   r.Streamline.DLSSG.Enable=1   — движок не поднимает DLSS-G по своей настройке
#   r.DynamicRes.OperationMode=0  — динамическое разрешение (bUseDynamicResolution=True) душит FG
# CVar кладутся в пользовательский Engine.ini, секция [SystemSettings] — она перекрывает настройки
# меню и не трогает файлы установки.
#
# Откат: удалить Engine.ini (stalker2-cleanup-experiments.ps1).
#
#   szcli exec 123456 -f tools\recipes\client\stalker2-force-dlssg.ps1 --in-session

$ErrorActionPreference = 'SilentlyContinue'
$cfgDir = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows'
$eng    = Join-Path $cfgDir 'Engine.ini'
$appId  = '1643320'

'=== 1. кладу CVar-переопределения ==='
$body = @'
[SystemSettings]
r.Streamline.DLSSG.Enable=1
r.DynamicRes.OperationMode=0
'@
Set-Content -Path $eng -Value $body -Encoding UTF8
Get-Content $eng | ForEach-Object { '   ' + $_ }

'=== 2. перезапускаю игру ==='
foreach ($p in (Get-Process Stalker2, Stalker2-Win64-Shipping)) { $null = $p.CloseMainWindow() }
Start-Sleep -Seconds 8
foreach ($p in (Get-Process Stalker2, Stalker2-Win64-Shipping)) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 5
Start-Process "steam://rungameid/$appId"
Start-Sleep -Seconds 60
$new = Get-Process Stalker2-Win64-Shipping
if ($new) { '   игра поднялась: pid {0}, старт {1:HH:mm:ss}, RAM {2:N0} МБ' -f $new.Id, $new.StartTime, ($new.WorkingSet64/1MB) }
else      { '   игра не поднялась' }
