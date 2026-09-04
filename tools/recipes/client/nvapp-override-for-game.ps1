$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что NVIDIA App навязала ИМЕННО ЭТОЙ игре: сопоставление id игры с маской DLSS-override (СЗ 123456).
#
# Грабля: ngxdlssoverridestate.json — плоский словарь «числовой id → маски stateMaskSR/RR/FG», без
# названий. Понять, чей это id, можно только через ApplicationStorage.json, где у той же игры есть
# и id, и DisplayName. Без сопоставления файл выглядит нечитаемой кашей и его пропускают.
# Ненулевая stateMaskFG = NVIDIA App вмешивается в генерацию кадров поверх настроек игры.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvapp-override-for-game.ps1

$ErrorActionPreference = 'SilentlyContinue'
$nv  = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App\NvBackend"
$app = Join-Path $nv 'ApplicationStorage.json'
$ovr = Join-Path $nv 'ngxdlssoverridestate.json'

'=== запись игры в ApplicationStorage ==='
$raw = Get-Content $app -Raw
$data = $raw | ConvertFrom-Json
$apps = $data.Applications
if (-not $apps) { $apps = $data.applications }
$hits = $apps | Where-Object { ($_ | ConvertTo-Json -Depth 6) -match 'Chornobyl|Chernobyl|STALKER 2' }
foreach ($h in $hits) {
    '   ---'
    $h.PSObject.Properties | Where-Object { $_.Name -match 'Id|DisplayName|ShortName|InstallDirectory|Platform' } |
        ForEach-Object { '   {0,-22} = {1}' -f $_.Name, ($_.Value -join ', ') }
}

'=== маски override для найденных id ==='
$state = Get-Content $ovr -Raw | ConvertFrom-Json
foreach ($h in $hits) {
    foreach ($prop in $h.PSObject.Properties) {
        if ($prop.Name -notmatch 'Id$|^Id') { continue }
        $key = [string]$prop.Value
        if ($state.PSObject.Properties.Name -contains $key) {
            $s = $state.$key
            '   id {0} ({1}): SR={2} RR={3} FG={4} scan={5}' -f $key, $prop.Name, $s.stateMaskSR, $s.stateMaskRR, $s.stateMaskFG, $s.gameScanMask
            if ($s.stateMaskFG -ne 0) { '      >>> NVIDIA App держит override генерации кадров для этой игры' }
        }
    }
}

'=== сколько игр вообще под override ==='
$all = $state.PSObject.Properties
'   записей всего: {0}' -f $all.Count
'   с ненулевой маской FG: {0}' -f (($all | Where-Object { $_.Value.stateMaskFG -ne 0 }).Count)
$all | Where-Object { $_.Value.stateMaskFG -ne 0 } | Select-Object -First 10 |
    ForEach-Object { '      id {0}: FG={1} SR={2}' -f $_.Name, $_.Value.stateMaskFG, $_.Value.stateMaskSR }

'=== кладу sl.interposer.json ещё и в корень игры (Streamline читает рабочий каталог) ==='
$root = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl'
$json = @{ showConsole = $false; logLevel = 2; logPath = 'C:\Users\nekit\AppData\Local\Temp\sl-logs' } | ConvertTo-Json
Set-Content (Join-Path $root 'sl.interposer.json') -Value $json -Encoding UTF8
'   {0}\sl.interposer.json записан' -f $root
