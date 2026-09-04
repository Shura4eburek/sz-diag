$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Проверка гипотезы: динамическое разрешение душит генерацию кадров (СЗ 123456).
#
# Почему сюда пришли: измерением установлено, что FG не работает (презентов ровно столько же,
# сколько кадров движка), при этом настройка в игре включена, HAGS on, файлы DLSS целы, а плагин
# DLSS-G в процесс загружен — то есть игра фичу запрашивает, а Streamline её не отдаёт.
# У DLSS-G есть документированные причины отказа, и одна из них прямо про нашу конфигурацию:
# динамическое разрешение. В GameUserSettings.ini стоит bUseDynamicResolution=True.
#
# CVar r.DynamicRes.OperationMode=0 через Engine.ini не помог — GameUserSettings::ApplySettings
# включает динамическое разрешение позже и перекрывает CVar. Поэтому правим саму настройку.
#
# Откат: рядом кладётся GameUserSettings.ini.szdiag-bak.
#
#   szcli exec 123456 -f tools\recipes\client\stalker2-dynres-off-test.ps1 --in-session

$ErrorActionPreference = 'SilentlyContinue'
$cfg   = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\GameUserSettings.ini'
$bak   = "$cfg.szdiag-bak"
$appId = '1643320'

'=== 1. бэкап и правка настройки ==='
if (-not (Test-Path $bak)) { Copy-Item $cfg $bak; '   бэкап: {0}' -f $bak }
$txt = Get-Content $cfg -Raw
$txt = $txt -replace 'bUseDynamicResolution=True', 'bUseDynamicResolution=False'
Set-Content $cfg -Value $txt -Encoding UTF8 -NoNewline
Get-Content $cfg | Select-String -Pattern 'bUseDynamicResolution|bUseVSync|FullscreenMode|FrameRateLimit' |
    ForEach-Object { '   ' + $_.Line.Trim() }

'=== 2. перезапуск игры ==='
foreach ($p in (Get-Process Stalker2, Stalker2-Win64-Shipping)) { $null = $p.CloseMainWindow() }
Start-Sleep -Seconds 8
foreach ($p in (Get-Process Stalker2, Stalker2-Win64-Shipping)) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 5
Start-Process "steam://rungameid/$appId"
Start-Sleep -Seconds 75
$new = Get-Process Stalker2-Win64-Shipping
if ($new) { '   игра поднялась: pid {0}, RAM {1:N0} МБ' -f $new.Id, ($new.WorkingSet64/1MB) }
else      { '   игра не поднялась' }

'=== 3. что осталось в конфиге после старта ==='
Get-Content $cfg | Select-String -Pattern 'bUseDynamicResolution' | ForEach-Object { '   ' + $_.Line.Trim() }
Get-Content 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\GSCReXFrameGeneration.ini' |
    ForEach-Object { '   ' + $_ }
