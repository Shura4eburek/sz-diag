$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Убрать всё, что диагностика по FG положила в игру (СЗ 123456).
#
# Что снимается: sl.interposer.json (включал лог Streamline — в шиппинг-сборке он так и не
# завёлся), пользовательский Engine.ini с CVar-переопределениями (r.Streamline.DLSSG.Enable,
# r.DynamicRes.OperationMode), бэкап GameUserSettings. Инструменты в Desktop\client\tools
# (presentmon, nvinspector) остаются до закрытия СЗ — их снимет szcli close.
#
#   szcli exec <СЗ> -f tools\recipes\client\stalker2-cleanup-experiments.ps1

$ErrorActionPreference = 'SilentlyContinue'
$targets = @(
    'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\sl.interposer.json',
    'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\Stalker2\Binaries\Win64\sl.interposer.json',
    'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\Engine.ini',
    'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\GameUserSettings.ini.szdiag-bak',
    'C:\Windows\Temp\szdiag-nvprofiles.xml',
    'C:\Windows\Temp\nvp-out.txt',
    'C:\Windows\Temp\nvp-err.txt'
)
foreach ($t in $targets) {
    if (Test-Path $t) { Remove-Item $t -Force; '   удалено: {0}' -f $t }
    else              { '   уже нет:  {0}' -f $t }
}

'=== папка логов Streamline ==='
$slDir = 'C:\Users\nekit\AppData\Local\Temp\sl-logs'
if (Test-Path $slDir) { Remove-Item $slDir -Recurse -Force; '   удалена: {0}' -f $slDir }

'=== проверка: что осталось в конфигах игры ==='
Get-ChildItem 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows' |
    ForEach-Object { '   {0,-34} {1:dd.MM HH:mm}' -f $_.Name, $_.LastWriteTime }
