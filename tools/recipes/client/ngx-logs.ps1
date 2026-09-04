$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что говорит сам NGX/Streamline о создании фич DLSS и DLSS-G (СЗ 123456).
#
# Грабля: PresentMon показал displayed == presented (92.3 fps обе), то есть кадры НЕ генерируются,
# хотя в конфиге игры Method=DLSSG/Mode=On2x и модули DLSS-G в процесс загружены. Значит фича
# создаётся и отклоняется — причина пишется в логи NGX драйвера, а не в лог игры.
#
#   szcli exec <СЗ> -f tools\recipes\client\ngx-logs.ps1

$ErrorActionPreference = 'SilentlyContinue'

$dirs = @(
    "$env:ProgramData\NVIDIA\NGX\logs",
    "$env:ProgramData\NVIDIA Corporation\NGX\logs",
    "$env:LOCALAPPDATA\NVIDIA\NGX\logs",
    "$env:LOCALAPPDATA\NVIDIA Corporation\NGX",
    "$env:ProgramData\NVIDIA\NGX"
)
'=== где вообще есть логи NGX ==='
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { continue }
    Get-ChildItem $d -Recurse -Include '*.log','*.txt' -Depth 3 |
        Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
        ForEach-Object { '   {0}   {1:dd.MM.yyyy HH:mm}  {2:N0} б' -f $_.FullName, $_.LastWriteTime, $_.Length }
}

'=== свежие логи: строки про dlssg / отказы ==='
$logs = foreach ($d in $dirs) {
    if (Test-Path $d) { Get-ChildItem $d -Recurse -Include '*.log' -Depth 3 }
}
$logs = $logs | Sort-Object LastWriteTime -Descending | Select-Object -First 4
foreach ($l in $logs) {
    "   -- $($l.FullName)"
    Get-Content $l.FullName -Tail 400 |
        Select-String -Pattern 'dlssg|dlss_g|frame ?gen|not supported|unsupported|fail|error|denied|disabled|VRAM|reflex' |
        Select-Object -Last 40 | ForEach-Object { '      ' + $_.Line.Trim() }
}

'=== логи Streamline рядом с игрой и в профиле пользователя ==='
Get-ChildItem 'C:\Users\nekit\AppData\Local\Temp','C:\Users\nekit\Documents','E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl' -Recurse -Include 'sl.log','streamline*.log','nvngx*.log' -Depth 4 |
    ForEach-Object { '   {0}  {1:dd.MM.yyyy HH:mm}  {2:N0} б' -f $_.FullName, $_.LastWriteTime, $_.Length }

'=== состояние DLSS-override в реестре драйвера ==='
foreach ($k in 'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NGXCore','HKCU:\SOFTWARE\NVIDIA Corporation\Global\NGXCore','HKLM:\SOFTWARE\NVIDIA Corporation\Global\NGXUpdater') {
    if (Test-Path $k) {
        "   -- $k"
        (Get-ItemProperty $k).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } |
            ForEach-Object { '      {0,-34} = {1}' -f $_.Name, $_.Value }
    }
}
