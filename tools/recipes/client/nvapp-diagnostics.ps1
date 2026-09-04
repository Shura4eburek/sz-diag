$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Логи NVIDIA App: что приложение применяло к игре (СЗ 123456).
#
# Зачем: настройки профиля драйвера (Low Latency Mode, V-Sync, DLSS override) лежат в бинарной
# базе nvdrsdb0.bin и удалённо не читаются — nvidiaProfileInspector 3.x умеет экспорт только из
# GUI, а разбор бинаря вслепую даёт мусор. Единственный текстовый след применённых настроек —
# собственные логи NVIDIA App.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvapp-diagnostics.ps1

$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App"

'=== файлы логов ==='
Get-ChildItem $root -Recurse -Include '*.log','*.txt' -Depth 4 |
    Sort-Object LastWriteTime -Descending | Select-Object -First 15 |
    ForEach-Object { '   {0,-64} {1:dd.MM HH:mm}  {2:N0} б' -f ($_.FullName -replace [regex]::Escape($root),'…'), $_.LastWriteTime, $_.Length }

'=== строки про нашу игру и про генерацию кадров ==='
$logs = Get-ChildItem $root -Recurse -Include '*.log' -Depth 4 | Sort-Object LastWriteTime -Descending | Select-Object -First 6
foreach ($l in $logs) {
    $hits = Get-Content $l.FullName -Tail 3000 |
        Select-String -Pattern 'stalker|chornobyl|frame ?gen|dlssg|dlss.?g|override|latency|vsync|profile' |
        Select-Object -Last 25
    if (-not $hits) { continue }
    "   -- $($l.Name)"
    $hits | ForEach-Object { '      ' + $_.Line.Trim() }
}
