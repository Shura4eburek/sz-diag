$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Попала ли игра в deny-list NVIDIA App по генерации кадров (СЗ 123456).
#
# В backend.log встречаются строки вида:
#   app_detector  Following app is DenyListed for FG, trying to disable it - <localId>
# У нашей игры localId 1339802291 (из ApplicationStorage.json). Если он там есть — NVIDIA App
# сама гасит FG-override для этой игры. Если нет — след ложный, и настройки NGXOverrideFG в логе
# относятся к другим приложениям из общего цикла.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvapp-fg-denylist.ps1

$ErrorActionPreference = 'SilentlyContinue'
$log = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App\NvBackend\backend.log"
$id  = '1339802291'

'=== упоминания localId игры ==='
Get-Content $log | Select-String -Pattern $id -SimpleMatch |
    Select-Object -Last 20 | ForEach-Object { '   ' + $_.Line.Trim() }

'=== все строки deny-list по FG ==='
Get-Content $log | Select-String -Pattern 'DenyListed for FG' |
    ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique |
    Select-Object -First 20 | ForEach-Object { '   ' + $_ }

'=== файл deny-list драйвера ==='
$deny = 'C:\ProgramData\NVIDIA\NGX\models\config\versions\1\files\nvngx_deny_list.txt'
if (Test-Path $deny) {
    $i = Get-Item $deny
    '   {0}  {1:N0} б  {2:dd.MM HH:mm}' -f $i.Name, $i.Length, $i.LastWriteTime
    Get-Content $deny | Select-Object -First 20 | ForEach-Object { '   ' + $_ }
}

'=== серверный конфиг NGX: секция dlssg ==='
$srv = 'C:\ProgramData\NVIDIA\NGX\models\config\versions\2\files\nvngx_server_config.txt'
if (Test-Path $srv) {
    $lines = Get-Content $srv
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\[dlssg\]') {
            $lines[$i..([Math]::Min($i + 15, $lines.Count - 1))] | ForEach-Object { '   ' + $_ }
        }
    }
}
