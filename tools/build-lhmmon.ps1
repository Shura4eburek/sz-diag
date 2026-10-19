# Сборка lhmmon — приборного логгера сенсоров для клиентских машин.
#
# Зачем скрипт: бинаря lhmmon нет ни в одной поставке, его собирали ad-hoc и потеряли —
# на 162003 (15.09.2026) выяснилось, что `szcli push --list` такого инструмента не знает
# вообще, а `start-sensors.ps1` советует доставить то, чего на хосте нет (бэклог п.270).
# Теперь источник живёт в tools\lhmmon, а этот скрипт кладёт свежий бинарь в раздачу.
#
# Проект собирается ОТДЕЛЬНО от солюшена (в SzDiag.sln не входит): это инструмент для
# клиента, а не часть системы, и тянуть LibreHardwareMonitorLib в `dotnet build` корня
# незачем.
#
#   .\tools\build-lhmmon.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "tools\lhmmon\lhmmon.csproj"
$out  = Join-Path $root "tools\lhmmon\publish"
$dest = Join-Path $root "client-tools\lhmmon"

Write-Host "-- собираю lhmmon (self-contained win-x64, single-file)"
dotnet publish $proj -c Release -o $out | Out-Null

$exe = Join-Path $out "lhmmon.exe"
if (-not (Test-Path $exe)) { throw "publish не дал lhmmon.exe — смотри вывод dotnet publish" }

New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item $exe $dest -Force
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "-- готово: $dest\lhmmon.exe ($size МБ)"
Write-Host "   доставка на клиента: szcli push <СЗ> lhmmon"
Write-Host "   запуск на клиенте:   szcli exec <СЗ> -f tools\recipes\client\start-sensors.ps1"
